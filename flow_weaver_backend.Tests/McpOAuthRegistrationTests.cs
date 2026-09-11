using System.Net;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Mcp;
using flow_weaver_backend.Services.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The parts of the MCP OAuth handshake the existing suite doesn't reach:
// dynamic client registration, token refresh, the CSRF nonce, and the
// discovery fallbacks.
//
// The security-relevant bit is that the registration endpoint comes from the
// remote server's OWN metadata — it is attacker-influenced, so it has to pass
// the SSRF guard before we POST anything to it.
public class McpOAuthRegistrationTests
{

    private sealed class ScriptedUrlGuard : IUrlGuard
    {
        public string? Reject { get; set; }
        public List<(string Url, bool AllowPrivate)> Checked { get; } = new();

        public void EnsureSafe(string url, bool allowPrivate = false)
        {
            Checked.Add((url, allowPrivate));
            if (Reject is not null) throw new InvalidOperationException(Reject);
        }

        // MCP is HTTP-only, so nothing here should ever take the host path.
        public void EnsureHostSafe(string host, bool allowPrivate = false)
            => throw new InvalidOperationException("unexpected EnsureHostSafe for host " + host);
    }

    // Discovery probes several well-known paths in order, so responses are
    // routed by URL rather than returned unconditionally.
    private static FakeHttpMessageHandler Routing(Dictionary<string, (HttpStatusCode, string)> routes)
        => new(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            foreach (var (fragment, response) in routes)
            {
                if (url.Contains(fragment, StringComparison.Ordinal))
                    return new HttpResponseMessage(response.Item1)
                    {
                        Content = new StringContent(response.Item2, Encoding.UTF8, "application/json"),
                    };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });

    private static McpOAuthService Build(FakeHttpMessageHandler http, ScriptedUrlGuard? guard = null)
        => new(new FakeHttpClientFactory(http), guard ?? new ScriptedUrlGuard(),
               NullLogger<McpOAuthService>.Instance);

    private static McpServer Server(string url = "https://mcp.test/sse", bool allowPrivate = false)
        => new()
        {
            McpServerId = Guid.NewGuid(),
            Name = "github-mcp",
            Url = url,
            Transport = "http",
            AuthType = "oauth",
            AllowPrivateNetwork = allowPrivate,
            Enabled = true,
            IsActive = true,
        };

    private static FakeHttpMessageHandler Ok(string body) => new(HttpStatusCode.OK, body);

    private static McpAuthConfig Auth(
        string? tokenEndpoint = null, string? authorizationEndpoint = null,
        string? clientId = null, string? clientSecret = null)
        => new()
        {
            TokenEndpoint = tokenEndpoint,
            AuthorizationEndpoint = authorizationEndpoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
        };

    // ─── Nonce ──────────────────────────────────────────────────────────

    // The nonce is the CSRF defence on the OAuth callback; a predictable one
    // would let an attacker complete someone else's authorization.
    [Fact]
    public void Nonce_IsFreshAndUrlSafe()
    {
        var service = Build(Ok("{}"));

        var nonces = Enumerable.Range(0, 20).Select(_ => service.GenerateNonce()).ToList();

        Assert.Equal(nonces.Count, nonces.Distinct().Count());
        Assert.All(nonces, n =>
        {
            Assert.NotEmpty(n);
            Assert.DoesNotContain("=", n);
            Assert.DoesNotContain("/", n);
            Assert.DoesNotContain("+", n);
        });
    }

    // ─── Discovery fallbacks ────────────────────────────────────────────

    // RFC 9728: a protected resource can delegate to a DIFFERENT
    // authorization server, and discovery has to follow that pointer rather
    // than assuming the MCP server issues its own tokens.
    [Fact]
    public async Task Discovery_FollowsTheProtectedResourcePointer()
    {
        var http = Routing(new()
        {
            ["/.well-known/oauth-protected-resource"] =
                (HttpStatusCode.OK, """{"authorization_servers":["https://idp.test/"]}"""),
            ["idp.test/.well-known/oauth-authorization-server"] =
                (HttpStatusCode.OK, """{"token_endpoint":"https://idp.test/token"}"""),
        });

        var meta = await Build(http).ResolveMetadataAsync(Server(), Auth());

        Assert.Equal("https://idp.test/token", meta.TokenEndpoint);
    }

    // OIDC servers publish the same fields under a different well-known path.
    [Fact]
    public async Task Discovery_FallsBackToTheOpenIdConfiguration()
    {
        var http = Routing(new()
        {
            ["/.well-known/openid-configuration"] =
                (HttpStatusCode.OK, """{"token_endpoint":"https://mcp.test/oidc/token"}"""),
        });

        var meta = await Build(http).ResolveMetadataAsync(Server(), Auth());

        Assert.Equal("https://mcp.test/oidc/token", meta.TokenEndpoint);
    }

    // A metadata document with no token endpoint is unusable, and so is one
    // that can't be reached at all — both produce an error naming the field
    // the admin should set by hand.
    [Fact]
    public async Task Discovery_ADocumentWithoutATokenEndpointIsRejected()
    {
        var http = Routing(new()
        {
            ["/.well-known/oauth-authorization-server"] =
                (HttpStatusCode.OK, """{"authorization_endpoint":"https://mcp.test/authorize"}"""),
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(http).ResolveMetadataAsync(Server(), Auth()));

        Assert.Contains("token_endpoint", ex.Message);
    }

    [Fact]
    public async Task Discovery_AnUnparseableServerUrlIsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(Ok("{}")).ResolveMetadataAsync(Server(url: "not a url"), Auth()));
    }

    // A manually configured endpoint wins outright — that is what lets an
    // admin point at a server publishing no metadata at all.
    [Fact]
    public async Task Discovery_AManualEndpointSkipsEveryRoundTrip()
    {
        var http = Ok("{}");

        var meta = await Build(http).ResolveMetadataAsync(
            Server(), Auth(tokenEndpoint: "  https://auth.test/token  "));

        Assert.Equal("https://auth.test/token", meta.TokenEndpoint);
        Assert.Empty(http.Requests);
    }

    // ─── Dynamic client registration ────────────────────────────────────

    [Fact]
    public async Task Registration_TheEndpointGoesThroughTheSsrfGuard()
    {
        var guard = new ScriptedUrlGuard();

        await Build(Ok("""{"client_id":"abc"}"""), guard).RegisterClientAsync(
            Server(), "https://mcp.test/oauth/register", "https://app.test/callback");

        Assert.Equal("https://mcp.test/oauth/register", Assert.Single(guard.Checked).Url);
    }

    [Fact]
    public async Task Registration_ARefusedUrlAbortsBeforeAnyCall()
    {
        var guard = new ScriptedUrlGuard { Reject = "blocked: link-local address" };
        var http = Ok("""{"client_id":"abc"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(http, guard).RegisterClientAsync(
            Server(), "http://169.254.169.254/register", "https://app.test/callback"));

        Assert.Empty(http.Requests);
    }

    // A server the admin marked as internal opts into private ranges; the
    // flag has to reach the guard or an on-prem MCP server can never work.
    [Fact]
    public async Task Registration_TheServersPrivateNetworkOptInReachesTheGuard()
    {
        var guard = new ScriptedUrlGuard();

        await Build(Ok("""{"client_id":"abc"}"""), guard).RegisterClientAsync(
            Server(allowPrivate: true), "http://10.0.0.5/register", "https://app.test/callback");

        Assert.True(Assert.Single(guard.Checked).AllowPrivate);
    }

    // We register as a PUBLIC client: a secret would have to survive the
    // browser redirect, so PKCE replaces it.
    [Fact]
    public async Task Registration_RegistersAsAPublicPkceClient()
    {
        var http = Ok("""{"client_id":"abc"}""");

        await Build(http).RegisterClientAsync(
            Server(), "https://mcp.test/register", "https://app.test/callback");

        using var doc = JsonDocument.Parse(http.RequestBodies[^1]);
        Assert.Equal("none", doc.RootElement.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal("https://app.test/callback",
            doc.RootElement.GetProperty("redirect_uris").EnumerateArray().Single().GetString());
        Assert.Contains("refresh_token",
            doc.RootElement.GetProperty("grant_types").EnumerateArray().Select(g => g.GetString()));
    }

    [Fact]
    public async Task Registration_ReturnsTheIssuedCredentials()
    {
        var http = Ok("""{"client_id":"abc","client_secret":"shh"}""");

        var (clientId, clientSecret) = await Build(http).RegisterClientAsync(
            Server(), "https://mcp.test/register", "https://app.test/callback");

        Assert.Equal("abc", clientId);
        Assert.Equal("shh", clientSecret);
    }

    // A public client legitimately gets no secret back.
    [Fact]
    public async Task Registration_AnAbsentSecretIsNotAnError()
    {
        var (clientId, clientSecret) = await Build(Ok("""{"client_id":"abc"}""")).RegisterClientAsync(
            Server(), "https://mcp.test/register", "https://app.test/callback");

        Assert.Equal("abc", clientId);
        Assert.Null(clientSecret);
    }

    // The upstream status and body ride along, because "registration failed"
    // on its own leaves the admin with nothing to act on.
    [Fact]
    public async Task Registration_AnUpstreamFailureCarriesTheStatusAndBody()
    {
        var http = new FakeHttpMessageHandler(
            HttpStatusCode.BadRequest, """{"error":"invalid_redirect_uri"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Build(http).RegisterClientAsync(
            Server(), "https://mcp.test/register", "https://app.test/callback"));

        Assert.Contains("400", ex.Message);
        Assert.Contains("invalid_redirect_uri", ex.Message);
    }

    // A 200 with no client_id is unusable and must not read as success.
    [Fact]
    public async Task Registration_AResponseWithoutAClientIdIsRejected()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(Ok("""{"client_secret":"shh"}""")).RegisterClientAsync(
                Server(), "https://mcp.test/register", "https://app.test/callback"));

        Assert.Contains("client_id", ex.Message);
    }

    // ─── Token refresh ──────────────────────────────────────────────────

    private static McpOAuthMetadata Meta(string tokenEndpoint = "https://auth.test/token")
        => new(null, tokenEndpoint, null, null);

    [Fact]
    public async Task Refresh_PostsTheRefreshGrant()
    {
        var http = Ok("""{"access_token":"new-token","token_type":"Bearer","expires_in":3600}""");

        var tokens = await Build(http).RefreshAsync(
            Server(), Meta(), Auth(clientId: "abc"), "old-refresh");

        var body = http.RequestBodies[^1];
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("refresh_token=old-refresh", body);
        Assert.Equal("new-token", tokens.AccessToken);
    }

    // The client id identifies which registration the refresh belongs to.
    [Fact]
    public async Task Refresh_CarriesTheClientId()
    {
        var http = Ok("""{"access_token":"t","token_type":"Bearer"}""");

        await Build(http).RefreshAsync(
            Server(), Meta(), Auth(clientId: "abc", clientSecret: "shh"), "old");

        Assert.Contains("client_id=abc", http.RequestBodies[^1]);
    }

    [Fact]
    public async Task Refresh_AnUpstreamRejectionThrows()
    {
        var http = new FakeHttpMessageHandler(
            HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(http).RefreshAsync(
            Server(), Meta(), Auth(clientId: "abc"), "expired"));
    }

    // A rotated refresh token must surface, or the next refresh presents one
    // the server already invalidated.
    [Fact]
    public async Task Refresh_ARotatedRefreshTokenIsReturned()
    {
        var http = Ok("""
            {"access_token":"a","token_type":"Bearer","refresh_token":"rotated","expires_in":60}
            """);

        var tokens = await Build(http).RefreshAsync(
            Server(), Meta(), Auth(clientId: "abc"), "old");

        Assert.Equal("rotated", tokens.RefreshToken);
    }
}
