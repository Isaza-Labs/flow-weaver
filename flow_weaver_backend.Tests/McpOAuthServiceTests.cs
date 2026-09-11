using System.Net;
using System.Security.Cryptography;
using System.Text;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Mcp;
using flow_weaver_backend.Services.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// F2: the OAuth 2.1 client mechanics — PKCE, metadata discovery, and the token
// grants (against a scripted HTTP handler).
public class McpOAuthServiceTests
{
    private static McpServer Server() => new() { McpServerId = Guid.NewGuid(), Url = "https://mcp.example.com/rpc" };

    private static McpOAuthService Svc(FakeHttpMessageHandler handler)
        => new(new FakeHttpClientFactory(handler), new AllowAllUrlGuard(), NullLogger<McpOAuthService>.Instance);

    [Fact]
    public void GeneratePkce_produces_base64url_verifier_and_s256_challenge()
    {
        var (verifier, challenge) = Svc(new FakeHttpMessageHandler(HttpStatusCode.OK)).GeneratePkce();

        Assert.Equal(43, verifier.Length); // 32 bytes base64url, no padding
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);
        Assert.DoesNotContain('=', verifier);

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, challenge);
    }

    [Fact]
    public async Task ClientCredentials_posts_grant_and_parses_tokens()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"access_token":"AT","refresh_token":"RT","expires_in":3600}""");
        var svc = Svc(handler);
        var auth = new McpAuthConfig { ClientId = "cid", ClientSecret = "sec", TokenEndpoint = "https://as/token" };

        var meta = await svc.ResolveMetadataAsync(Server(), auth);
        var tokens = await svc.ClientCredentialsAsync(Server(), meta, auth);

        Assert.Equal("AT", tokens.AccessToken);
        Assert.Equal("RT", tokens.RefreshToken);
        Assert.True(tokens.ExpiresAt > DateTime.UtcNow.AddSeconds(3500) && tokens.ExpiresAt < DateTime.UtcNow.AddSeconds(3700));

        var body = handler.RequestBodies[0];
        Assert.Contains("grant_type=client_credentials", body);
        Assert.Contains("client_id=cid", body);
    }

    [Fact]
    public async Task ExchangeCode_sends_code_and_verifier()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"access_token":"AT"}""");
        var svc = Svc(handler);
        var auth = new McpAuthConfig { ClientId = "cid", TokenEndpoint = "https://as/token" };
        var meta = await svc.ResolveMetadataAsync(Server(), auth);

        await svc.ExchangeCodeAsync(Server(), meta, auth, "the-code", "the-verifier", "https://cb", default);

        var body = handler.RequestBodies[0];
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code=the-code", body);
        Assert.Contains("code_verifier=the-verifier", body);
    }

    [Fact]
    public async Task Token_endpoint_error_throws()
    {
        var svc = Svc(new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, "nope"));
        var auth = new McpAuthConfig { ClientId = "cid", TokenEndpoint = "https://as/token" };
        var meta = await svc.ResolveMetadataAsync(Server(), auth);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ClientCredentialsAsync(Server(), meta, auth));
    }

    [Fact]
    public async Task ResolveMetadata_discovers_via_well_known()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            string body = url.Contains("oauth-protected-resource")
                ? """{"authorization_servers":["https://as.example.com"]}"""
                : url.Contains("oauth-authorization-server") || url.Contains("openid-configuration")
                    ? """{"authorization_endpoint":"https://as.example.com/authorize","token_endpoint":"https://as.example.com/token","registration_endpoint":"https://as.example.com/register"}"""
                    : "";
            return string.IsNullOrEmpty(body)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var meta = await Svc(handler).ResolveMetadataAsync(Server(), new McpAuthConfig());

        Assert.Equal("https://as.example.com/token", meta.TokenEndpoint);
        Assert.Equal("https://as.example.com/authorize", meta.AuthorizationEndpoint);
        Assert.Equal("https://as.example.com/register", meta.RegistrationEndpoint);
    }
}

file sealed class AllowAllUrlGuard : IUrlGuard
{
    public void EnsureSafe(string url, bool allowPrivate = false) { }
    public void EnsureHostSafe(string host, bool allowPrivate = false) { }
}
