using System.Text;
using System.Text.Json;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Mcp;
using flow_weaver_backend.Services.Net;

namespace flow_weaver_backend.Tests;

// F0: McpConnectionFactory turns an McpServer + its encrypted auth into request
// headers, and applies the SSRF guard / URL validation. Plus McpAuthConfigCodec
// round-trip.
public class McpConnectionFactoryTests
{
    private static McpServer Server(string authType, McpAuthConfig auth, ICredentialEncryptionService crypto)
        => new()
        {
            McpServerId = Guid.NewGuid(),
            Name = "srv",
            Url = "https://mcp.example.com/rpc",
            AuthType = authType,
            AuthConfigEncrypted = McpAuthConfigCodec.Encrypt(auth, crypto),
        };

    // ── codec ──

    [Fact]
    public void Codec_roundtrips_all_fields()
    {
        var crypto = new ReversibleCrypto();
        var original = new McpAuthConfig
        {
            ApiKeyHeader = "X-Key",
            ApiKey = "abc",
            Token = "tok",
            Username = "u",
            Password = "p",
            SecretHeaders = new() { ["H"] = "v" },
            ClientId = "cid",
            ClientSecret = "csec",
            Scopes = new() { "a", "b" },
            AccessToken = "at",
            RefreshToken = "rt",
        };
        var cipher = McpAuthConfigCodec.Encrypt(original, crypto);
        var back = McpAuthConfigCodec.Decrypt(cipher, crypto);

        Assert.Equal("X-Key", back.ApiKeyHeader);
        Assert.Equal("abc", back.ApiKey);
        Assert.Equal("tok", back.Token);
        Assert.Equal("u", back.Username);
        Assert.Equal("p", back.Password);
        Assert.Equal("v", back.SecretHeaders!["H"]);
        Assert.Equal("cid", back.ClientId);
        Assert.Equal(new[] { "a", "b" }, back.Scopes);
        Assert.Equal("at", back.AccessToken);
        Assert.Equal("rt", back.RefreshToken);
    }

    [Fact]
    public void Codec_decrypts_null_and_garbage_to_empty()
    {
        var crypto = new ReversibleCrypto();
        Assert.NotNull(McpAuthConfigCodec.Decrypt(null, crypto));
        var garbage = Encoding.UTF8.GetBytes("this is not json");
        var back = McpAuthConfigCodec.Decrypt(garbage, crypto);
        Assert.Null(back.Token); // empty config, no throw
    }

    // ── header building ──

    [Fact]
    public async Task None_adds_no_auth_header()
    {
        var crypto = new ReversibleCrypto();
        var conn = await new McpConnectionFactory(crypto, new RecordingUrlGuard(), new StubTokenService())
            .CreateAsync(Server("none", new McpAuthConfig(), crypto));
        Assert.False(conn.Headers.ContainsKey("Authorization"));
        Assert.Equal(new Uri("https://mcp.example.com/rpc"), conn.Endpoint);
    }

    [Fact]
    public async Task ApiKey_uses_default_header_when_unset()
    {
        var crypto = new ReversibleCrypto();
        var conn = await new McpConnectionFactory(crypto, new RecordingUrlGuard(), new StubTokenService())
            .CreateAsync(Server("api_key", new McpAuthConfig { ApiKey = "k" }, crypto));
        Assert.Equal("k", conn.Headers["X-API-Key"]);
    }

    [Fact]
    public async Task ApiKey_uses_custom_header()
    {
        var crypto = new ReversibleCrypto();
        var conn = await new McpConnectionFactory(crypto, new RecordingUrlGuard(), new StubTokenService())
            .CreateAsync(Server("api_key", new McpAuthConfig { ApiKeyHeader = "X-Custom", ApiKey = "k" }, crypto));
        Assert.Equal("k", conn.Headers["X-Custom"]);
        Assert.False(conn.Headers.ContainsKey("X-API-Key"));
    }

    [Fact]
    public async Task Bearer_sets_authorization()
    {
        var crypto = new ReversibleCrypto();
        var conn = await new McpConnectionFactory(crypto, new RecordingUrlGuard(), new StubTokenService())
            .CreateAsync(Server("bearer", new McpAuthConfig { Token = "sekret" }, crypto));
        Assert.Equal("Bearer sekret", conn.Headers["Authorization"]);
    }

    [Fact]
    public async Task Basic_sets_authorization_from_username_and_password()
    {
        var crypto = new ReversibleCrypto();
        var conn = await new McpConnectionFactory(crypto, new RecordingUrlGuard(), new StubTokenService())
            .CreateAsync(Server("basic", new McpAuthConfig { Username = "user", Password = "pass" }, crypto));
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));
        Assert.Equal(expected, conn.Headers["Authorization"]);
    }

    [Fact]
    public async Task Basic_without_credentials_sets_no_auth_header()
    {
        var crypto = new ReversibleCrypto();
        var conn = await new McpConnectionFactory(crypto, new RecordingUrlGuard(), new StubTokenService())
            .CreateAsync(Server("basic", new McpAuthConfig(), crypto));
        Assert.False(conn.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Headers_auth_merges_secret_headers()
    {
        var crypto = new ReversibleCrypto();
        var auth = new McpAuthConfig { SecretHeaders = new() { ["X-Secret"] = "s", ["X-Two"] = "2" } };
        var conn = await new McpConnectionFactory(crypto, new RecordingUrlGuard(), new StubTokenService())
            .CreateAsync(Server("headers", auth, crypto));
        Assert.Equal("s", conn.Headers["X-Secret"]);
        Assert.Equal("2", conn.Headers["X-Two"]);
    }

    [Fact]
    public async Task Oauth_sets_bearer_from_token_service()
    {
        var crypto = new ReversibleCrypto();
        var conn = await new McpConnectionFactory(crypto, new RecordingUrlGuard(), new StubTokenService { Token = "resolved" })
            .CreateAsync(Server("oauth_client_credentials", new McpAuthConfig(), crypto));
        Assert.Equal("Bearer resolved", conn.Headers["Authorization"]);
    }

    [Fact]
    public async Task Oauth_without_token_sets_no_auth_header()
    {
        var crypto = new ReversibleCrypto();
        var conn = await new McpConnectionFactory(crypto, new RecordingUrlGuard(), new StubTokenService { Token = null })
            .CreateAsync(Server("oauth_authorization_code", new McpAuthConfig(), crypto));
        Assert.False(conn.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Static_non_secret_headers_are_merged()
    {
        var crypto = new ReversibleCrypto();
        var server = Server("none", new McpAuthConfig(), crypto);
        server.Headers = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["X-Static"] = "v" });
        var conn = await new McpConnectionFactory(crypto, new RecordingUrlGuard(), new StubTokenService()).CreateAsync(server);
        Assert.Equal("v", conn.Headers["X-Static"]);
    }

    // ── URL + SSRF ──

    [Fact]
    public async Task Bad_url_throws()
    {
        var crypto = new ReversibleCrypto();
        var server = Server("none", new McpAuthConfig(), crypto);
        server.Url = "not-a-url";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new McpConnectionFactory(crypto, new RecordingUrlGuard(), new StubTokenService()).CreateAsync(server));
    }

    [Fact]
    public async Task Non_http_scheme_throws()
    {
        var crypto = new ReversibleCrypto();
        var server = Server("none", new McpAuthConfig(), crypto);
        server.Url = "ftp://mcp.example.com";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new McpConnectionFactory(crypto, new RecordingUrlGuard(), new StubTokenService()).CreateAsync(server));
    }

    [Fact]
    public async Task Ssrf_guard_receives_allow_private_flag()
    {
        var crypto = new ReversibleCrypto();
        var guard = new RecordingUrlGuard();
        var server = Server("none", new McpAuthConfig(), crypto);
        server.AllowPrivateNetwork = true;
        await new McpConnectionFactory(crypto, guard, new StubTokenService()).CreateAsync(server);
        Assert.Equal(server.Url, guard.LastUrl);
        Assert.True(guard.LastAllowPrivate);
    }

    [Fact]
    public async Task Ssrf_blocked_url_propagates()
    {
        var crypto = new ReversibleCrypto();
        var guard = new RecordingUrlGuard { ThrowBlocked = true };
        var server = Server("none", new McpAuthConfig(), crypto);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new McpConnectionFactory(crypto, guard, new StubTokenService()).CreateAsync(server));
    }
}

// Reversible non-encryption for tests (round-trips plaintext through UTF-8 bytes).
file sealed class ReversibleCrypto : ICredentialEncryptionService
{
    public byte[]? Encrypt(string? plaintext)
        => plaintext is null ? null : Encoding.UTF8.GetBytes(plaintext);

    public string? Decrypt(byte[]? ciphertext)
        => ciphertext is null ? null : Encoding.UTF8.GetString(ciphertext);
}

file sealed class RecordingUrlGuard : IUrlGuard
{
    public string? LastUrl;
    public bool LastAllowPrivate;
    public bool ThrowBlocked;

    public void EnsureSafe(string url, bool allowPrivate = false)
    {
        LastUrl = url;
        LastAllowPrivate = allowPrivate;
        if (ThrowBlocked) throw new InvalidOperationException("blocked: " + url);
    }

    // MCP is HTTP-only, so nothing here should ever take the host path.
    public void EnsureHostSafe(string host, bool allowPrivate = false)
        => throw new InvalidOperationException("unexpected EnsureHostSafe for host " + host);
}

file sealed class StubTokenService : IMcpTokenService
{
    public string? Token = "resolved-token";
    public Task<string?> GetValidAccessTokenAsync(McpServer server, CancellationToken ct = default)
        => Task.FromResult(Token);
}
