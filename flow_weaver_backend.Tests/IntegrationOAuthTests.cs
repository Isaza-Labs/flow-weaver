using System.Net;
using flow_weaver_backend.Services.Integration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Tests;

// OAuth2 client-credentials for integrations (auth method
// `oauth2_client_credentials`): the token service grants + caches, the async
// applier layers the Bearer token over the sync builder's headers. Mirrors
// the MCP servers' oauth_client_credentials support.
public class IntegrationOAuthTests
{
    private const string TokenResponse =
        """{"access_token":"tok-123","token_type":"Bearer","expires_in":3600}""";

    private static IntegrationModel OAuthIntegration(
        string clientSecret = "s3cret",
        string scope = "read write",
        string audience = "")
        => new()
        {
            IntegrationId = Guid.NewGuid(),
            Name = "idp-backed",
            BaseURL = "https://api.example.com",
            AuthConfig = TestJson.Element($$"""
                {
                  "method": "oauth2_client_credentials",
                  "token_url": "https://idp.example.com/oauth/token",
                  "client_id": "cid-1",
                  "client_secret": "{{clientSecret}}",
                  "scope": "{{scope}}",
                  "audience": "{{audience}}"
                }
                """),
            Headers = TestJson.Element("""{"X-Env":"prod"}"""),
            IsActive = true,
        };

    private static IntegrationOAuthTokenService TokenService(FakeHttpMessageHandler handler)
        => new(new FakeHttpClientFactory(handler), new PassUrlGuard(),
               new MemoryCache(new MemoryCacheOptions()),
               NullLogger<IntegrationOAuthTokenService>.Instance);

    // ── token service ───────────────────────────────────────────────────

    [Fact]
    public async Task Grant_posts_client_credentials_form_and_returns_token()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, TokenResponse);
        var svc = TokenService(handler);

        var token = await svc.GetAccessTokenAsync(OAuthIntegration(audience: "https://api.example.com"));

        Assert.Equal("tok-123", token);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://idp.example.com/oauth/token", req.RequestUri!.ToString());
        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("grant_type=client_credentials", body);
        Assert.Contains("client_id=cid-1", body);
        Assert.Contains("client_secret=s3cret", body);
        Assert.Contains("scope=read+write", body);
        Assert.Contains("audience=", body);
    }

    [Fact]
    public async Task Grant_is_cached_until_expiry()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, TokenResponse);
        var svc = TokenService(handler);
        var integration = OAuthIntegration();

        await svc.GetAccessTokenAsync(integration);
        await svc.GetAccessTokenAsync(integration);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Invalidate_forces_a_regrant()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, TokenResponse);
        var svc = TokenService(handler);
        var integration = OAuthIntegration();

        await svc.GetAccessTokenAsync(integration);
        svc.Invalidate(integration.IntegrationId);
        await svc.GetAccessTokenAsync(integration);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Grant_failure_throws_with_status()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");
        var svc = TokenService(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetAccessTokenAsync(OAuthIntegration()));
        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid_client", ex.Message);
    }

    [Fact]
    public async Task Missing_token_url_is_an_actionable_error()
    {
        var svc = TokenService(new FakeHttpMessageHandler(HttpStatusCode.OK, TokenResponse));
        var integration = OAuthIntegration();
        integration.AuthConfig = TestJson.Element("""{"method":"oauth2_client_credentials","client_id":"cid-1"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.GetAccessTokenAsync(integration));
        Assert.Contains("token_url", ex.Message);
    }

    // ── async applier ───────────────────────────────────────────────────

    [Fact]
    public async Task Applier_sets_bearer_token_and_keeps_custom_headers()
    {
        var tokenHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, TokenResponse);
        var applier = TestAuth.Applier(tokenHandler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1/things");

        await applier.ApplyAsync(request, OAuthIntegration());

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("tok-123", request.Headers.Authorization?.Parameter);
        Assert.True(request.Headers.Contains("X-Env"));
    }

    [Fact]
    public async Task Applier_never_calls_the_token_endpoint_for_static_methods()
    {
        var tokenHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, TokenResponse);
        var applier = TestAuth.Applier(tokenHandler);
        var integration = new IntegrationModel
        {
            IntegrationId = Guid.NewGuid(),
            Name = "static",
            BaseURL = "https://api.example.com",
            AuthConfig = TestJson.Element("""{"method":"bearer","token":"static-tok"}"""),
            IsActive = true,
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1/things");

        await applier.ApplyAsync(request, integration);

        Assert.Equal("static-tok", request.Headers.Authorization?.Parameter);
        Assert.Empty(tokenHandler.Requests);
    }
}
