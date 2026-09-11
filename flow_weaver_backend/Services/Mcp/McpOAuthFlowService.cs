using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using Microsoft.AspNetCore.DataProtection;

namespace flow_weaver_backend.Services.Mcp;

public sealed record McpOAuthStartResult(string? AuthorizationUrl, string? Error);

// The authorization-code + PKCE redirect flow for MCP servers. `StartAsync`
// (admin) resolves metadata / DCR, mints PKCE + a signed state, persists the
// verifier, and returns the authorization_url. `HandleCallbackAsync`
// (anonymous, CSRF-checked via the signed state + stored nonce) exchanges the
// code for tokens and stores them encrypted. State is a purpose-scoped,
// time-limited DataProtection token, so no server-side session row is needed.
public interface IMcpOAuthFlowService
{
    Task<McpOAuthStartResult> StartAsync(Guid mcpServerId, string redirectUri, CancellationToken ct = default);
    Task<string> HandleCallbackAsync(string? code, string? state, string? error, string redirectUriFallback, CancellationToken ct = default);
}

public sealed class McpOAuthFlowService : IMcpOAuthFlowService
{
    private const string StatePurpose = "flow-weaver.mcp.oauth-state";
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    private readonly IMcpServerRepository _servers;
    private readonly IMcpOAuthService _oauth;
    private readonly ICredentialEncryptionService _crypto;
    private readonly ITimeLimitedDataProtector _stateProtector;
    private readonly IConfiguration _config;
    private readonly ILogger<McpOAuthFlowService> _logger;

    public McpOAuthFlowService(
        IMcpServerRepository servers,
        IMcpOAuthService oauth,
        ICredentialEncryptionService crypto,
        IDataProtectionProvider dataProtection,
        IConfiguration config,
        ILogger<McpOAuthFlowService> logger)
    {
        _servers = servers;
        _oauth = oauth;
        _crypto = crypto;
        _stateProtector = dataProtection.CreateProtector(StatePurpose).ToTimeLimitedDataProtector();
        _config = config;
        _logger = logger;
    }

    public async Task<McpOAuthStartResult> StartAsync(
        Guid mcpServerId, string redirectUri, CancellationToken ct = default)
    {
        var server = await _servers.GetByIdAsync(mcpServerId, activeOnly: true, tracking: true, ct);
        if (server is null) return new McpOAuthStartResult(null, "MCP server not found.");
        if (!string.Equals(server.AuthType, "oauth_authorization_code", StringComparison.OrdinalIgnoreCase))
            return new McpOAuthStartResult(null, "Authorize is only available for authorization-code OAuth servers.");

        var auth = McpAuthConfigCodec.Decrypt(server.AuthConfigEncrypted, _crypto);

        McpOAuthMetadata meta;
        try { meta = await _oauth.ResolveMetadataAsync(server, auth, ct); }
        catch (Exception ex) { return new McpOAuthStartResult(null, ex.Message); }

        if (string.IsNullOrEmpty(meta.AuthorizationEndpoint))
            return new McpOAuthStartResult(null, "no authorization_endpoint found; set it manually.");

        // Resolve client_id — Dynamic Client Registration if we have none and the AS offers it.
        if (string.IsNullOrEmpty(auth.ClientId))
        {
            if (string.IsNullOrEmpty(meta.RegistrationEndpoint))
                return new McpOAuthStartResult(null, "client_id is required (the AS has no registration endpoint).");
            try
            {
                var (cid, csec) = await _oauth.RegisterClientAsync(server, meta.RegistrationEndpoint!, redirectUri, ct);
                auth.ClientId = cid;
                if (!string.IsNullOrEmpty(csec)) auth.ClientSecret = csec;
            }
            catch (Exception ex) { return new McpOAuthStartResult(null, $"dynamic client registration failed: {ex.Message}"); }
        }

        var (verifier, challenge) = _oauth.GeneratePkce();
        var nonce = _oauth.GenerateNonce();
        auth.CodeVerifier = verifier;
        auth.StateNonce = nonce;
        auth.RedirectUri = redirectUri;
        auth.TokenEndpoint ??= meta.TokenEndpoint;
        auth.AuthorizationEndpoint ??= meta.AuthorizationEndpoint;

        server.AuthConfigEncrypted = McpAuthConfigCodec.Encrypt(auth, _crypto);
        server.Status = "needs_authorization";
        server.UpdatedAt = DateTime.UtcNow;
        await _servers.SaveChangesAsync(ct);

        var statePayload = JsonSerializer.Serialize(new { s = mcpServerId, n = nonce });
        var state = _stateProtector.Protect(statePayload, StateTtl);

        var url = BuildAuthorizationUrl(meta.AuthorizationEndpoint!, auth, redirectUri, state, challenge, meta);
        return new McpOAuthStartResult(url, null);
    }

    public async Task<string> HandleCallbackAsync(
        string? code, string? state, string? error, string redirectUriFallback, CancellationToken ct = default)
    {
        Guid serverId;
        string nonce;
        try
        {
            using var doc = JsonDocument.Parse(_stateProtector.Unprotect(state ?? string.Empty));
            var r = doc.RootElement;
            serverId = Guid.Parse(r.GetProperty("s").GetString()!);
            nonce = r.GetProperty("n").GetString()!;
        }
        catch
        {
            return FrontendUrl("error", "invalid_state");
        }

        var server = await _servers.GetByIdAsync(serverId, activeOnly: true, tracking: true, ct);
        if (server is null) return FrontendUrl("error", "server_not_found");
        var auth = McpAuthConfigCodec.Decrypt(server.AuthConfigEncrypted, _crypto);

        // Only a callback that matches a LIVE pending authorization (the one-time
        // verifier is still stored and the nonce matches) may mutate the server's
        // status. A stale or replayed — but validly signed, within-TTL — state
        // (e.g. after a successful authorize cleared the verifier) must not be
        // able to downgrade a healthy server to needs_authorization.
        var isPending = !string.IsNullOrEmpty(auth.CodeVerifier)
            && string.Equals(nonce, auth.StateNonce, StringComparison.Ordinal);
        if (!isPending) return FrontendUrl("error", "invalid_callback");

        if (!string.IsNullOrEmpty(error))
        {
            await MarkNeedsAuthAsync(server, ct);
            return FrontendUrl("error", error!);
        }
        if (string.IsNullOrEmpty(code))
        {
            await MarkNeedsAuthAsync(server, ct);
            return FrontendUrl("error", "invalid_callback");
        }

        try
        {
            var meta = await _oauth.ResolveMetadataAsync(server, auth, ct);
            var redirectUri = string.IsNullOrEmpty(auth.RedirectUri) ? redirectUriFallback : auth.RedirectUri!;
            var tokens = await _oauth.ExchangeCodeAsync(server, meta, auth, code!, auth.CodeVerifier!, redirectUri, ct);

            auth.AccessToken = tokens.AccessToken;
            auth.RefreshToken = tokens.RefreshToken;
            auth.ExpiresAt = tokens.ExpiresAt;
            auth.CodeVerifier = null;
            auth.StateNonce = null;

            server.AuthConfigEncrypted = McpAuthConfigCodec.Encrypt(auth, _crypto);
            server.Status = "ok";
            server.UpdatedAt = DateTime.UtcNow;
            await _servers.SaveChangesAsync(ct);

            return FrontendUrl("authorized", server.McpServerId.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mcp.oauth.callback_exchange_failed server={Server}", server.McpServerId);
            await MarkNeedsAuthAsync(server, ct);
            return FrontendUrl("error", "exchange_failed");
        }
    }

    private async Task MarkNeedsAuthAsync(McpServer server, CancellationToken ct)
    {
        server.Status = "needs_authorization";
        server.UpdatedAt = DateTime.UtcNow;
        await _servers.SaveChangesAsync(ct);
    }

    private static string BuildAuthorizationUrl(
        string authEndpoint, McpAuthConfig auth, string redirectUri, string state, string challenge, McpOAuthMetadata meta)
    {
        var scopes = auth.Scopes is { Count: > 0 } ? auth.Scopes : meta.ScopesSupported;
        var q = new List<string>
        {
            "response_type=code",
            "client_id=" + Uri.EscapeDataString(auth.ClientId ?? string.Empty),
            "redirect_uri=" + Uri.EscapeDataString(redirectUri),
            "state=" + Uri.EscapeDataString(state),
            "code_challenge=" + Uri.EscapeDataString(challenge),
            "code_challenge_method=S256",
        };
        if (scopes is { Count: > 0 }) q.Add("scope=" + Uri.EscapeDataString(string.Join(' ', scopes)));

        var sep = authEndpoint.Contains('?') ? "&" : "?";
        return authEndpoint + sep + string.Join('&', q);
    }

    private string FrontendUrl(string key, string value)
    {
        var origins = _config.GetSection("Cors:AllowedOrigins").Get<string[]>();
        var baseUrl = (origins is { Length: > 0 } ? origins[0] : "http://localhost:5173").TrimEnd('/');
        return $"{baseUrl}/admin/mcp-servers?mcp_{key}={Uri.EscapeDataString(value)}";
    }
}
