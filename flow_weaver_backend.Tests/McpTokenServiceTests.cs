using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// F2: McpTokenService — return a cached token, refresh/re-grant an expired one,
// and flip to needs_authorization when nothing can be resolved.
public class McpTokenServiceTests
{
    private static readonly FakeCrypto Crypto = new();

    private static (McpTokenService svc, StubMcpOAuthService oauth, AppDbContext db, Guid id)
        Setup(string authType, McpAuthConfig auth)
    {
        var db = TestDb.NewContext();
        var id = Guid.NewGuid();
        db.McpServers.Add(new McpServer
        {
            McpServerId = id, Name = "srv", Url = "https://x",
            AuthType = authType, Enabled = true, IsActive = true, Status = "ok",
            AuthConfigEncrypted = McpAuthConfigCodec.Encrypt(auth, Crypto),
        });
        db.SaveChanges();
        var repo = new McpServerRepository(db);
        var oauth = new StubMcpOAuthService();
        var svc = new McpTokenService(repo, oauth, Crypto, NullLogger<McpTokenService>.Instance);
        return (svc, oauth, db, id);
    }

    private static async Task<McpServer> Load(AppDbContext db, Guid id)
        => (await new McpServerRepository(db).GetByIdAsync(id, activeOnly: true, tracking: false))!;

    [Fact]
    public async Task Returns_cached_token_when_valid()
    {
        var (svc, oauth, db, id) = Setup("oauth_client_credentials",
            new McpAuthConfig { AccessToken = "cached", ExpiresAt = DateTime.UtcNow.AddHours(1) });

        var token = await svc.GetValidAccessTokenAsync(await Load(db, id));

        Assert.Equal("cached", token);
        Assert.Equal(0, oauth.ClientCredentialsCalls);
    }

    [Fact]
    public async Task Client_credentials_regrants_when_expired()
    {
        var (svc, oauth, db, id) = Setup("oauth_client_credentials",
            new McpAuthConfig { AccessToken = "old", ExpiresAt = DateTime.UtcNow.AddMinutes(-1) });
        oauth.Tokens = new McpOAuthTokens("NEW", null, DateTime.UtcNow.AddHours(1));

        var token = await svc.GetValidAccessTokenAsync(await Load(db, id));

        Assert.Equal("NEW", token);
        Assert.Equal(1, oauth.ClientCredentialsCalls);
        var stored = McpAuthConfigCodec.Decrypt(db.McpServers.Single().AuthConfigEncrypted, Crypto);
        Assert.Equal("NEW", stored.AccessToken);
        Assert.Equal("ok", db.McpServers.Single().Status);
    }

    [Fact]
    public async Task Client_credentials_regrants_when_expiry_unknown()
    {
        // No expires_in ⇒ ExpiresAt null. A stale cached token must NOT be served
        // forever — client-credentials re-grants (cheap, stateless).
        var (svc, oauth, db, id) = Setup("oauth_client_credentials",
            new McpAuthConfig { AccessToken = "cached-no-expiry", ExpiresAt = null });
        oauth.Tokens = new McpOAuthTokens("FRESH", null, DateTime.UtcNow.AddHours(1));

        var token = await svc.GetValidAccessTokenAsync(await Load(db, id));

        Assert.Equal("FRESH", token);
        Assert.Equal(1, oauth.ClientCredentialsCalls);
    }

    [Fact]
    public async Task Authorization_code_refreshes_with_refresh_token()
    {
        var (svc, oauth, db, id) = Setup("oauth_authorization_code",
            new McpAuthConfig { AccessToken = "old", ExpiresAt = DateTime.UtcNow.AddMinutes(-1), RefreshToken = "rt" });
        oauth.Tokens = new McpOAuthTokens("REFRESHED", "rt2", DateTime.UtcNow.AddHours(1));

        var token = await svc.GetValidAccessTokenAsync(await Load(db, id));

        Assert.Equal("REFRESHED", token);
        Assert.Equal(1, oauth.RefreshCalls);
    }

    [Fact]
    public async Task Authorization_code_without_refresh_needs_authorization()
    {
        var (svc, _, db, id) = Setup("oauth_authorization_code",
            new McpAuthConfig { ExpiresAt = DateTime.UtcNow.AddMinutes(-1) });

        var token = await svc.GetValidAccessTokenAsync(await Load(db, id));

        Assert.Null(token);
        Assert.Equal("needs_authorization", db.McpServers.Single().Status);
    }

    [Fact]
    public async Task Refresh_failure_flips_to_needs_authorization()
    {
        var (svc, oauth, db, id) = Setup("oauth_authorization_code",
            new McpAuthConfig { AccessToken = "old", ExpiresAt = DateTime.UtcNow.AddMinutes(-1), RefreshToken = "rt" });
        oauth.ThrowOnGrant = new InvalidOperationException("token endpoint returned 400");

        var token = await svc.GetValidAccessTokenAsync(await Load(db, id));

        Assert.Null(token);
        Assert.Equal("needs_authorization", db.McpServers.Single().Status);
    }
}

// Configurable IMcpOAuthService double, shared with McpOAuthFlowServiceTests.
internal sealed class StubMcpOAuthService : IMcpOAuthService
{
    public McpOAuthTokens Tokens = new("AT", "RT", null);
    public McpOAuthMetadata Metadata = new("https://as/authorize", "https://as/token", null, null);
    public Exception? ThrowOnGrant;
    public int ClientCredentialsCalls;
    public int RefreshCalls;
    public int ExchangeCalls;
    public (string verifier, string challenge) Pkce = ("VERIFIER-0000000000000000000000000000000", "CHALLENGE");
    public string Nonce = "NONCE-123";

    public Task<McpOAuthMetadata> ResolveMetadataAsync(McpServer server, McpAuthConfig auth, CancellationToken ct = default)
        => Task.FromResult(Metadata);

    public Task<(string clientId, string? clientSecret)> RegisterClientAsync(
        McpServer server, string registrationEndpoint, string redirectUri, CancellationToken ct = default)
        => Task.FromResult<(string, string?)>(("dcr-client", null));

    public Task<McpOAuthTokens> ClientCredentialsAsync(McpServer server, McpOAuthMetadata meta, McpAuthConfig auth, CancellationToken ct = default)
    {
        ClientCredentialsCalls++;
        if (ThrowOnGrant is not null) throw ThrowOnGrant;
        return Task.FromResult(Tokens);
    }

    public Task<McpOAuthTokens> ExchangeCodeAsync(
        McpServer server, McpOAuthMetadata meta, McpAuthConfig auth,
        string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        ExchangeCalls++;
        if (ThrowOnGrant is not null) throw ThrowOnGrant;
        return Task.FromResult(Tokens);
    }

    public Task<McpOAuthTokens> RefreshAsync(McpServer server, McpOAuthMetadata meta, McpAuthConfig auth, string refreshToken, CancellationToken ct = default)
    {
        RefreshCalls++;
        if (ThrowOnGrant is not null) throw ThrowOnGrant;
        return Task.FromResult(Tokens);
    }

    public (string verifier, string challenge) GeneratePkce() => Pkce;
    public string GenerateNonce() => Nonce;
}
