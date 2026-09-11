using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Services.Mcp;

// Resolves a valid OAuth access token for a server, refreshing (or, for
// client-credentials, re-granting) when the cached one is missing/expired, and
// persisting the result. Used by McpConnectionFactory on the connect path
// (lazy refresh). On failure it flips Status → needs_authorization.
public interface IMcpTokenService
{
    Task<string?> GetValidAccessTokenAsync(McpServer server, CancellationToken ct = default);
}

public sealed class McpTokenService : IMcpTokenService
{
    // Refresh a little before the token actually expires.
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private readonly IMcpServerRepository _servers;
    private readonly IMcpOAuthService _oauth;
    private readonly ICredentialEncryptionService _crypto;
    private readonly ILogger<McpTokenService> _logger;

    public McpTokenService(
        IMcpServerRepository servers,
        IMcpOAuthService oauth,
        ICredentialEncryptionService crypto,
        ILogger<McpTokenService> logger)
    {
        _servers = servers;
        _oauth = oauth;
        _crypto = crypto;
        _logger = logger;
    }

    public async Task<string?> GetValidAccessTokenAsync(McpServer server, CancellationToken ct = default)
    {
        var auth = McpAuthConfigCodec.Decrypt(server.AuthConfigEncrypted, _crypto);

        // A cached token is trusted only while it is known-unexpired. For
        // client-credentials, an unknown expiry (the AS omitted expires_in) means
        // "re-grant" — the grant is cheap and stateless — rather than risk serving
        // a dead token forever (there is no Authorize button to self-heal it).
        var isClientCredentials =
            string.Equals(server.AuthType, "oauth_client_credentials", StringComparison.OrdinalIgnoreCase);
        var cachedIsValid = !string.IsNullOrEmpty(auth.AccessToken)
            && (isClientCredentials
                ? auth.ExpiresAt > DateTime.UtcNow.Add(ExpirySkew)
                : auth.ExpiresAt is null || auth.ExpiresAt > DateTime.UtcNow.Add(ExpirySkew));
        if (cachedIsValid) return auth.AccessToken;

        try
        {
            var meta = await _oauth.ResolveMetadataAsync(server, auth, ct);
            McpOAuthTokens tokens;

            if (string.Equals(server.AuthType, "oauth_client_credentials", StringComparison.OrdinalIgnoreCase))
            {
                tokens = await _oauth.ClientCredentialsAsync(server, meta, auth, ct);
            }
            else if (!string.IsNullOrEmpty(auth.RefreshToken))
            {
                tokens = await _oauth.RefreshAsync(server, meta, auth, auth.RefreshToken!, ct);
            }
            else
            {
                // Authorization-code server that was never authorized (or whose
                // token expired with no refresh token) — the admin must re-run
                // the Authorize flow.
                await SetStatusAsync(server, "needs_authorization", ct);
                return null;
            }

            await PersistTokensAsync(server, auth, tokens, ct);
            return tokens.AccessToken;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Transient timeout — don't downgrade the server's status; the next
            // call retries.
            _logger.LogWarning("mcp.oauth.token_refresh_timeout server={Server}", server.McpServerId);
            return null;
        }
        catch (Exception ex)
        {
            // A concurrent caller may have already refreshed (e.g. our refresh
            // token was rotated out from under us). Prefer their freshly-persisted
            // token before declaring the server unauthorized.
            var fresh = await _servers.GetByIdAsync(server.McpServerId, activeOnly: true, tracking: false, ct);
            if (fresh is not null)
            {
                var freshAuth = McpAuthConfigCodec.Decrypt(fresh.AuthConfigEncrypted, _crypto);
                if (!string.IsNullOrEmpty(freshAuth.AccessToken)
                    && (freshAuth.ExpiresAt is null || freshAuth.ExpiresAt > DateTime.UtcNow.Add(ExpirySkew)))
                {
                    server.AuthConfigEncrypted = fresh.AuthConfigEncrypted;
                    server.Status = "ok";
                    return freshAuth.AccessToken;
                }
            }

            _logger.LogWarning(ex, "mcp.oauth.token_resolve_failed server={Server}", server.McpServerId);
            await SetStatusAsync(server, "needs_authorization", ct);
            return null;
        }
    }

    private async Task PersistTokensAsync(McpServer server, McpAuthConfig auth, McpOAuthTokens tokens, CancellationToken ct)
    {
        auth.AccessToken = tokens.AccessToken;
        // Keep the existing refresh token when the AS didn't rotate it.
        if (!string.IsNullOrEmpty(tokens.RefreshToken)) auth.RefreshToken = tokens.RefreshToken;
        auth.ExpiresAt = tokens.ExpiresAt;

        var tracked = await _servers.GetByIdAsync(server.McpServerId, activeOnly: true, tracking: true, ct);
        if (tracked is null) return;

        tracked.AuthConfigEncrypted = McpAuthConfigCodec.Encrypt(auth, _crypto);
        tracked.Status = "ok";
        tracked.UpdatedAt = DateTime.UtcNow;
        await _servers.SaveChangesAsync(ct);

        // Reflect on the in-memory copy the connect path holds.
        server.AuthConfigEncrypted = tracked.AuthConfigEncrypted;
        server.Status = "ok";
    }

    private async Task SetStatusAsync(McpServer server, string status, CancellationToken ct)
    {
        var tracked = await _servers.GetByIdAsync(server.McpServerId, activeOnly: true, tracking: true, ct);
        if (tracked is null) return;
        tracked.Status = status;
        tracked.LastCheckedAt = DateTime.UtcNow;
        tracked.UpdatedAt = DateTime.UtcNow;
        await _servers.SaveChangesAsync(ct);
        server.Status = status;
    }
}
