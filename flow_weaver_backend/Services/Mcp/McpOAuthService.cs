using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Net;

namespace flow_weaver_backend.Services.Mcp;

// Resolved OAuth 2.1 endpoints for an MCP server's authorization server.
public sealed record McpOAuthMetadata(
    string? AuthorizationEndpoint,
    string TokenEndpoint,
    string? RegistrationEndpoint,
    IReadOnlyList<string>? ScopesSupported);

// Tokens obtained from a grant.
public sealed record McpOAuthTokens(string AccessToken, string? RefreshToken, DateTime? ExpiresAt);

// The OAuth 2.1 client mechanics: metadata discovery, Dynamic Client
// Registration, PKCE, and the token-endpoint grants (client-credentials,
// authorization-code, refresh). Stateless HTTP — persistence of the returned
// tokens is the caller's job (McpTokenService / the callback endpoint).
public interface IMcpOAuthService
{
    Task<McpOAuthMetadata> ResolveMetadataAsync(McpServer server, McpAuthConfig auth, CancellationToken ct = default);
    Task<(string clientId, string? clientSecret)> RegisterClientAsync(
        McpServer server, string registrationEndpoint, string redirectUri, CancellationToken ct = default);
    Task<McpOAuthTokens> ClientCredentialsAsync(
        McpServer server, McpOAuthMetadata meta, McpAuthConfig auth, CancellationToken ct = default);
    Task<McpOAuthTokens> ExchangeCodeAsync(
        McpServer server, McpOAuthMetadata meta, McpAuthConfig auth,
        string code, string codeVerifier, string redirectUri, CancellationToken ct = default);
    Task<McpOAuthTokens> RefreshAsync(
        McpServer server, McpOAuthMetadata meta, McpAuthConfig auth, string refreshToken, CancellationToken ct = default);

    (string verifier, string challenge) GeneratePkce();
    string GenerateNonce();
}

public sealed class McpOAuthService : IMcpOAuthService
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IUrlGuard _urlGuard;
    private readonly ILogger<McpOAuthService> _logger;

    public McpOAuthService(IHttpClientFactory httpFactory, IUrlGuard urlGuard, ILogger<McpOAuthService> logger)
    {
        _httpFactory = httpFactory;
        _urlGuard = urlGuard;
        _logger = logger;
    }

    // ── PKCE + state ──────────────────────────────────────────────────────

    public (string verifier, string challenge) GeneratePkce()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var verifier = Base64Url(bytes);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public string GenerateNonce()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    // ── metadata discovery ────────────────────────────────────────────────

    public async Task<McpOAuthMetadata> ResolveMetadataAsync(McpServer server, McpAuthConfig auth, CancellationToken ct = default)
    {
        // Manually-configured endpoints win — no discovery round-trip needed.
        if (!string.IsNullOrWhiteSpace(auth.TokenEndpoint))
        {
            return new McpOAuthMetadata(auth.AuthorizationEndpoint, auth.TokenEndpoint!.Trim(), null, auth.Scopes);
        }

        var discovered = await DiscoverAsync(server, ct)
            ?? throw new InvalidOperationException(
                "could not discover the OAuth authorization server; set token_endpoint (and authorization_endpoint) manually.");
        return discovered;
    }

    private async Task<McpOAuthMetadata?> DiscoverAsync(McpServer server, CancellationToken ct)
    {
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var serverUri)) return null;
        var origin = serverUri.GetLeftPart(UriPartial.Authority);

        // RFC 9728: the protected resource points at its authorization server(s).
        string authServer = origin;
        var prm = await TryGetJsonAsync(server, $"{origin}/.well-known/oauth-protected-resource", ct);
        if (prm is { } p && p.TryGetProperty("authorization_servers", out var arr)
            && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            && arr[0].GetString() is { Length: > 0 } first)
        {
            authServer = first.TrimEnd('/');
        }

        // RFC 8414 / OIDC: the AS metadata document.
        var asm = await TryGetJsonAsync(server, $"{authServer}/.well-known/oauth-authorization-server", ct)
                  ?? await TryGetJsonAsync(server, $"{authServer}/.well-known/openid-configuration", ct);
        if (asm is not { } m) return null;

        var tokenEndpoint = GetStr(m, "token_endpoint");
        if (string.IsNullOrEmpty(tokenEndpoint)) return null;

        return new McpOAuthMetadata(
            GetStr(m, "authorization_endpoint"),
            tokenEndpoint!,
            GetStr(m, "registration_endpoint"),
            GetStrArray(m, "scopes_supported"));
    }

    // ── Dynamic Client Registration (RFC 7591) ────────────────────────────

    public async Task<(string clientId, string? clientSecret)> RegisterClientAsync(
        McpServer server, string registrationEndpoint, string redirectUri, CancellationToken ct = default)
    {
        _urlGuard.EnsureSafe(registrationEndpoint, allowPrivate: server.AllowPrivateNetwork);

        var payload = JsonSerializer.Serialize(new
        {
            client_name = "flow-weaver",
            redirect_uris = new[] { redirectUri },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none", // public client + PKCE
        });

        var http = ClientFor(server);
        using var cts = Budget(ct);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(registrationEndpoint, content, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"dynamic client registration failed ({(int)resp.StatusCode}): {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var clientId = GetStr(doc.RootElement, "client_id");
        if (string.IsNullOrEmpty(clientId))
            throw new InvalidOperationException("registration response had no client_id.");
        return (clientId!, GetStr(doc.RootElement, "client_secret"));
    }

    // ── grants ────────────────────────────────────────────────────────────

    public Task<McpOAuthTokens> ClientCredentialsAsync(
        McpServer server, McpOAuthMetadata meta, McpAuthConfig auth, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
        AddClientAuth(form, auth);
        AddScope(form, auth, meta);
        return PostTokenAsync(server, meta.TokenEndpoint, form, ct);
    }

    public Task<McpOAuthTokens> ExchangeCodeAsync(
        McpServer server, McpOAuthMetadata meta, McpAuthConfig auth,
        string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        };
        AddClientAuth(form, auth);
        return PostTokenAsync(server, meta.TokenEndpoint, form, ct);
    }

    public Task<McpOAuthTokens> RefreshAsync(
        McpServer server, McpOAuthMetadata meta, McpAuthConfig auth, string refreshToken, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };
        AddClientAuth(form, auth);
        return PostTokenAsync(server, meta.TokenEndpoint, form, ct);
    }

    private async Task<McpOAuthTokens> PostTokenAsync(
        McpServer server, string tokenEndpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        _urlGuard.EnsureSafe(tokenEndpoint, allowPrivate: server.AllowPrivateNetwork);

        var http = ClientFor(server);
        using var cts = Budget(ct);
        using var content = new FormUrlEncodedContent(form);
        using var resp = await http.PostAsync(tokenEndpoint, content, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("mcp.oauth.token_failed server={Server} status={Status}", server.McpServerId, (int)resp.StatusCode);
            throw new InvalidOperationException($"token endpoint returned {(int)resp.StatusCode}: {Truncate(body, 300)}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = GetStr(root, "access_token");
        if (string.IsNullOrEmpty(accessToken))
            throw new InvalidOperationException("token endpoint response had no access_token.");

        DateTime? expiresAt = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var secs)
            ? DateTime.UtcNow.AddSeconds(secs)
            : null;
        return new McpOAuthTokens(accessToken!, GetStr(root, "refresh_token"), expiresAt);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private HttpClient ClientFor(McpServer server)
        => _httpFactory.CreateClient(server.TLSSkipVerify ? McpHttpClients.Insecure : McpHttpClients.Secure);

    private static CancellationTokenSource Budget(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HttpTimeout);
        return cts;
    }

    private async Task<JsonElement?> TryGetJsonAsync(McpServer server, string url, CancellationToken ct)
    {
        try
        {
            _urlGuard.EnsureSafe(url, allowPrivate: server.AllowPrivateNetwork);
            var http = ClientFor(server);
            using var cts = Budget(ct);
            using var resp = await http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug("mcp.oauth.discovery_probe_failed url={Url} err={Err}", url, ex.Message);
            return null;
        }
    }

    private static void AddClientAuth(Dictionary<string, string> form, McpAuthConfig auth)
    {
        if (!string.IsNullOrEmpty(auth.ClientId)) form["client_id"] = auth.ClientId!;
        if (!string.IsNullOrEmpty(auth.ClientSecret)) form["client_secret"] = auth.ClientSecret!;
    }

    private static void AddScope(Dictionary<string, string> form, McpAuthConfig auth, McpOAuthMetadata meta)
    {
        var scopes = auth.Scopes is { Count: > 0 } ? auth.Scopes : meta.ScopesSupported;
        if (scopes is { Count: > 0 }) form["scope"] = string.Join(' ', scopes);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? GetStr(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string>? GetStrArray(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s) list.Add(s);
        return list.Count > 0 ? list : null;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
