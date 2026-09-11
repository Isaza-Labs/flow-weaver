using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Mcp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// F2: the authorization-code + PKCE redirect flow. Start mints a signed state +
// PKCE and persists the verifier; the callback validates state/nonce, exchanges
// the code, and stores tokens. State round-trips through one ephemeral protector.
public class McpOAuthFlowServiceTests
{
    private static readonly FakeCrypto Crypto = new();

    private static (McpOAuthFlowService svc, StubMcpOAuthService oauth, AppDbContext db, Guid id)
        Setup(string authType, McpAuthConfig auth)
    {
        var db = TestDb.NewContext();
        var id = Guid.NewGuid();
        db.McpServers.Add(new McpServer
        {
            McpServerId = id, Name = "srv", Url = "https://mcp.example.com",
            AuthType = authType, Enabled = true, IsActive = true, Status = "needs_config",
            AuthConfigEncrypted = McpAuthConfigCodec.Encrypt(auth, Crypto),
        });
        db.SaveChanges();

        var oauth = new StubMcpOAuthService();
        var svc = new McpOAuthFlowService(
            new McpServerRepository(db), oauth, Crypto,
            new EphemeralDataProtectionProvider(), new ConfigurationBuilder().Build(),
            NullLogger<McpOAuthFlowService>.Instance);
        return (svc, oauth, db, id);
    }

    private static string Extract(string url, string key)
        => Uri.UnescapeDataString(Regex.Match(url, $"[?&]{key}=([^&]+)").Groups[1].Value);

    [Fact]
    public async Task Start_rejects_non_authorization_code_servers()
    {
        var (svc, _, _, id) = Setup("oauth_client_credentials", new McpAuthConfig());
        var res = await svc.StartAsync(id, "https://backend/cb");
        Assert.NotNull(res.Error);
        Assert.Null(res.AuthorizationUrl);
    }

    [Fact]
    public async Task Start_builds_authorization_url_and_persists_verifier()
    {
        var (svc, oauth, db, id) = Setup("oauth_authorization_code", new McpAuthConfig { ClientId = "cid" });

        var res = await svc.StartAsync(id, "https://backend/cb");

        Assert.Null(res.Error);
        Assert.NotNull(res.AuthorizationUrl);
        Assert.StartsWith("https://as/authorize?", res.AuthorizationUrl);
        Assert.Contains("client_id=cid", res.AuthorizationUrl);
        Assert.Contains("code_challenge=CHALLENGE", res.AuthorizationUrl);
        Assert.Contains("code_challenge_method=S256", res.AuthorizationUrl);
        Assert.Contains("state=", res.AuthorizationUrl);

        var stored = McpAuthConfigCodec.Decrypt(db.McpServers.Single().AuthConfigEncrypted, Crypto);
        Assert.Equal(oauth.Pkce.verifier, stored.CodeVerifier);
        Assert.Equal(oauth.Nonce, stored.StateNonce);
        Assert.Equal("needs_authorization", db.McpServers.Single().Status);
    }

    [Fact]
    public async Task Callback_exchanges_code_and_stores_tokens()
    {
        var (svc, oauth, db, id) = Setup("oauth_authorization_code", new McpAuthConfig { ClientId = "cid" });
        var start = await svc.StartAsync(id, "https://backend/cb");
        var state = Extract(start.AuthorizationUrl!, "state");
        oauth.Tokens = new McpOAuthTokens("ACCESS", "REFRESH", DateTime.UtcNow.AddHours(1));

        var redirect = await svc.HandleCallbackAsync("the-code", state, null, "https://backend/cb");

        Assert.Contains($"mcp_authorized={id}", redirect);
        Assert.Equal(1, oauth.ExchangeCalls);
        var stored = McpAuthConfigCodec.Decrypt(db.McpServers.Single().AuthConfigEncrypted, Crypto);
        Assert.Equal("ACCESS", stored.AccessToken);
        Assert.Equal("REFRESH", stored.RefreshToken);
        Assert.Null(stored.CodeVerifier);   // one-time, cleared
        Assert.Null(stored.StateNonce);
        Assert.Equal("ok", db.McpServers.Single().Status);
    }

    [Fact]
    public async Task Callback_rejects_tampered_state()
    {
        var (svc, oauth, _, _) = Setup("oauth_authorization_code", new McpAuthConfig { ClientId = "cid" });

        var redirect = await svc.HandleCallbackAsync("code", "not-a-valid-state", null, "https://backend/cb");

        Assert.Contains("mcp_error=invalid_state", redirect);
        Assert.Equal(0, oauth.ExchangeCalls);
    }

    [Fact]
    public async Task Callback_with_error_marks_needs_authorization()
    {
        var (svc, oauth, db, id) = Setup("oauth_authorization_code", new McpAuthConfig { ClientId = "cid" });
        var start = await svc.StartAsync(id, "https://backend/cb");
        var state = Extract(start.AuthorizationUrl!, "state");

        var redirect = await svc.HandleCallbackAsync(null, state, "access_denied", "https://backend/cb");

        Assert.Contains("mcp_error=access_denied", redirect);
        Assert.Equal(0, oauth.ExchangeCalls);
        Assert.Equal("needs_authorization", db.McpServers.Single().Status);
    }
}
