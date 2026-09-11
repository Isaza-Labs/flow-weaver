using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Net;
using Microsoft.Extensions.Caching.Memory;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Services.Integration;

// Obtains and caches OAuth2 client-credentials access tokens for
// integrations whose auth method is `oauth2_client_credentials`. Mirrors the
// MCP servers' McpTokenService/McpOAuthService pair, trimmed to what a REST
// integration needs: no discovery (the admin supplies token_url), no
// authorization-code (workflows are headless — the grant must be
// non-interactive), no refresh tokens (the grant IS the refresh).
//
// Tokens live ONLY in IMemoryCache, keyed by integration id — never in
// Integration.AuthConfig. The config holds the client credentials; runtime
// state stays out of the row so an export/inspect of the integration can
// never leak a live token, and a restart simply re-grants.
public interface IIntegrationOAuthTokenService
{
    // Returns a valid access token, granting a fresh one when the cached one
    // is missing or near expiry. Throws InvalidOperationException with an
    // actionable message when the grant fails (bad credentials, unreachable
    // token endpoint, malformed response).
    Task<string> GetAccessTokenAsync(IntegrationModel integration, CancellationToken ct = default);

    // Drops the cached token so the next call re-grants — call after the
    // upstream rejects a request with 401 (token revoked mid-lifetime).
    void Invalidate(Guid integrationId);
}

public sealed class IntegrationOAuthTokenService : IIntegrationOAuthTokenService
{
    // Re-grant a little before the token actually expires.
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);
    // When the AS omits expires_in we still cache briefly: a per-device
    // fan-out fires many calls in seconds and the grant shouldn't run once
    // per call. Short enough that a rotated secret propagates quickly.
    private static readonly TimeSpan UnknownExpiryTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IUrlGuard _urlGuard;
    private readonly IMemoryCache _cache;
    private readonly ILogger<IntegrationOAuthTokenService> _logger;

    public IntegrationOAuthTokenService(
        IHttpClientFactory httpFactory,
        IUrlGuard urlGuard,
        IMemoryCache cache,
        ILogger<IntegrationOAuthTokenService> logger)
    {
        _httpFactory = httpFactory;
        _urlGuard = urlGuard;
        _cache = cache;
        _logger = logger;
    }

    private static string CacheKey(Guid integrationId) => $"integration-oauth-token:{integrationId}";

    public void Invalidate(Guid integrationId) => _cache.Remove(CacheKey(integrationId));

    public async Task<string> GetAccessTokenAsync(IntegrationModel integration, CancellationToken ct = default)
    {
        if (_cache.TryGetValue<string>(CacheKey(integration.IntegrationId), out var cached)
            && !string.IsNullOrEmpty(cached))
            return cached!;

        var auth = IntegrationAuthBuilder.TryParse(integration.AuthConfig)
            ?? throw new InvalidOperationException("integration auth_config is missing or malformed");
        if (string.IsNullOrWhiteSpace(auth.TokenUrl))
            throw new InvalidOperationException("oauth2_client_credentials requires token_url in auth_config");
        if (string.IsNullOrWhiteSpace(auth.ClientId))
            throw new InvalidOperationException("oauth2_client_credentials requires client_id in auth_config");

        // Same SSRF stance as every other outbound integration URL.
        _urlGuard.EnsureSafe(auth.TokenUrl, allowPrivate: integration.AllowPrivateNetwork);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = auth.ClientId,
        };
        if (!string.IsNullOrEmpty(auth.ClientSecret)) form["client_secret"] = auth.ClientSecret;
        if (!string.IsNullOrWhiteSpace(auth.Scope)) form["scope"] = auth.Scope.Trim();
        if (!string.IsNullOrWhiteSpace(auth.Audience)) form["audience"] = auth.Audience.Trim();

        var clientName = integration.TLSSkipVerify
            ? IntegrationHealthChecker.InsecureClientName
            : IntegrationHealthChecker.ClientName;
        var http = _httpFactory.CreateClient(clientName);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HttpTimeout);

        string body;
        int status;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            // Built as an explicit message (rather than PostAsync) so the
            // redirect guard sees the integration's private-network policy —
            // a token endpoint that 302s is validated per hop, and the
            // client_secret in the form body never follows a cross-origin hop
            // because 301/302/303 drop the body entirely.
            using var msg = new HttpRequestMessage(HttpMethod.Post, auth.TokenUrl) { Content = content };
            msg.WithPrivateNetworkPolicy(integration.AllowPrivateNetwork);
            using var resp = await http.SendAsync(msg, cts.Token);
            status = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "integration.oauth.token_failed integration_id={IntegrationId} status={Status}",
                    integration.IntegrationId, status);
                throw new InvalidOperationException(
                    $"OAuth token endpoint returned {status}: {Truncate(body, 300)}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("OAuth token endpoint request timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"OAuth token endpoint unreachable: {ex.Message}");
        }

        string accessToken;
        TimeSpan ttl;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            accessToken = root.TryGetProperty("access_token", out var at)
                && at.ValueKind == JsonValueKind.String
                && at.GetString() is { Length: > 0 } t
                    ? t
                    : throw new InvalidOperationException("OAuth token response had no access_token");

            ttl = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var secs) && secs > 0
                ? TimeSpan.FromSeconds(secs) - ExpirySkew
                : UnknownExpiryTtl;
            if (ttl < TimeSpan.FromSeconds(30)) ttl = TimeSpan.FromSeconds(30);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("OAuth token response was not valid JSON");
        }

        _cache.Set(CacheKey(integration.IntegrationId), accessToken, ttl);
        _logger.LogInformation(
            "integration.oauth.token_ok integration_id={IntegrationId} ttl_s={TtlSeconds}",
            integration.IntegrationId, (int)ttl.TotalSeconds);
        return accessToken;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
