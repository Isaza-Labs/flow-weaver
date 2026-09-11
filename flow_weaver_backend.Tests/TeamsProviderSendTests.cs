using System.Net;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Messaging.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Teams replies go out in two hops: acquire an AAD client-credentials token,
// then POST the activity to the serviceUrl the inbound activity carried. The
// security property worth pinning is that the AAD bearer is only ever sent to
// an https Bot Framework endpoint — serviceUrl originates from the inbound
// payload, so a malformed or forged value must not be able to exfiltrate it.
public class TeamsProviderSendTests
{
    // Routes the AAD token request and the activity POST to separate scripted
    // responses, so both hops are assertable independently.
    private sealed class TwoHopHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;
        public string TokenBody { get; set; } = """{"access_token":"aad-token-123"}""";
        public HttpStatusCode SendStatus { get; set; } = HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var isTokenCall = request.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.Ordinal);
            return isTokenCall
                ? new HttpResponseMessage(TokenStatus)
                {
                    Content = new StringContent(TokenBody, Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(SendStatus)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
        }

        public HttpRequestMessage TokenRequest => Requests.Single(r =>
            r.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.Ordinal));
        public HttpRequestMessage ActivityRequest => Requests.Single(r =>
            !r.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.Ordinal));
        public string TokenForm => Bodies[Requests.IndexOf(TokenRequest)];
        public string ActivityBody => Bodies[Requests.IndexOf(ActivityRequest)];
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static (TeamsProvider Provider, TwoHopHandler Handler) Build()
    {
        var handler = new TwoHopHandler();
        return (new TeamsProvider(new Factory(handler), NullLogger<TeamsProvider>.Instance), handler);
    }

    private static MessagingChannel Channel(string? appId = "app-123", string? tenantId = null)
    {
        var config = new Dictionary<string, string>();
        if (appId is not null) config["app_id"] = appId;
        if (tenantId is not null) config["tenant_id"] = tenantId;
        return new MessagingChannel
        {
            MessagingChannelId = Guid.NewGuid(),
            Provider = MessagingChannel.ProviderTeams,
            Name = "teams",
            ExternalConfig = TestJson.Element(JsonSerializer.Serialize(config)),
            IsActive = true,
        };
    }

    private static OutboundMessage Message(
        string thread = "https://smba.example/teams::19:conv", string text = "hello")
        => new() { ExternalThreadId = thread, Text = text };

    // ─── configuration guards ───────────────────────────────────────────

    [Fact]
    public async Task MissingAppSecretIsRefused()
    {
        var (provider, handler) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendAsync(Channel(), null, Message(), default));

        Assert.Contains("no app secret", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingAppIdIsRefused()
    {
        var (provider, handler) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendAsync(Channel(appId: null), "secret", Message(), default));

        Assert.Contains("no app_id", ex.Message);
        Assert.Empty(handler.Requests);
    }

    // ─── thread id handling ─────────────────────────────────────────────

    // The composite thread id is `serviceUrl::conversationId`; without the
    // separator there is nowhere to send.
    [Theory]
    [InlineData("no-separator-here")]
    [InlineData("")]
    public async Task MalformedThreadIdIsRefused(string thread)
    {
        var (provider, handler) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendAsync(Channel(), "secret", Message(thread: thread), default));

        Assert.Contains("missing serviceUrl/conversationId", ex.Message);
        Assert.Empty(handler.Requests);
    }

    // The AAD bearer must never leave over plaintext or to a non-https host —
    // serviceUrl comes from the inbound payload.
    [Theory]
    [InlineData("http://evil.example::19:conv")]
    [InlineData("ftp://evil.example::19:conv")]
    [InlineData("//evil.example::19:conv")]
    public async Task NonHttpsServiceUrlIsRefusedBeforeAnyTokenIsAcquired(string thread)
    {
        var (provider, handler) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendAsync(Channel(), "secret", Message(thread: thread), default));

        Assert.Contains("must be https", ex.Message);
        // Critically: the token was never even requested, so nothing to leak.
        Assert.Empty(handler.Requests);
    }

    // ─── the AAD token hop ──────────────────────────────────────────────

    [Fact]
    public async Task TokenIsRequestedWithClientCredentials()
    {
        var (provider, handler) = Build();

        await provider.SendAsync(Channel(appId: "app-123"), "the-secret", Message(), default);

        var form = handler.TokenForm;
        Assert.Contains("grant_type=client_credentials", form);
        Assert.Contains("client_id=app-123", form);
        Assert.Contains("client_secret=the-secret", form);
        Assert.Contains("api.botframework.com", Uri.UnescapeDataString(form));
    }

    // Without a tenant the shared botframework.com authority is used; with one
    // the request goes to that tenant's authority.
    [Fact]
    public async Task TokenAuthorityDefaultsToBotFramework()
    {
        var (provider, handler) = Build();

        await provider.SendAsync(Channel(), "secret", Message(), default);

        Assert.Equal("https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token",
            handler.TokenRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task ConfiguredTenantGetsItsOwnAuthority()
    {
        var (provider, handler) = Build();

        await provider.SendAsync(Channel(tenantId: "contoso-tenant"), "secret", Message(), default);

        Assert.Equal("https://login.microsoftonline.com/contoso-tenant/oauth2/v2.0/token",
            handler.TokenRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task TokenFailureAbortsBeforeSending()
    {
        var (provider, handler) = Build();
        handler.TokenStatus = HttpStatusCode.Unauthorized;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendAsync(Channel(), "secret", Message(), default));

        Assert.Contains("AAD token failed: 401", ex.Message);
        Assert.Single(handler.Requests);   // only the token call happened
    }

    [Theory]
    [InlineData("""{"token_type":"Bearer"}""")]
    [InlineData("""{"access_token":123}""")]
    [InlineData("""{}""")]
    public async Task TokenResponseWithoutAnAccessTokenIsRefused(string body)
    {
        var (provider, handler) = Build();
        handler.TokenBody = body;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendAsync(Channel(), "secret", Message(), default));

        Assert.Contains("missing access_token", ex.Message);
    }

    // ─── the activity hop ───────────────────────────────────────────────

    [Fact]
    public async Task ActivityIsPostedToTheConversationEndpoint()
    {
        var (provider, handler) = Build();

        await provider.SendAsync(
            Channel(), "secret", Message(thread: "https://smba.example/teams::19:conv"), default);

        var req = handler.ActivityRequest;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://smba.example/teams/v3/conversations/19:conv/activities",
            req.RequestUri!.ToString());
    }

    // A trailing slash on serviceUrl must not double up.
    [Fact]
    public async Task TrailingSlashOnServiceUrlIsNormalised()
    {
        var (provider, handler) = Build();

        await provider.SendAsync(
            Channel(), "secret", Message(thread: "https://smba.example/teams/::19:conv"), default);

        Assert.Equal("https://smba.example/teams/v3/conversations/19:conv/activities",
            handler.ActivityRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task TheAcquiredTokenIsUsedAsTheBearer()
    {
        var (provider, handler) = Build();
        handler.TokenBody = """{"access_token":"minted-token"}""";

        await provider.SendAsync(Channel(), "secret", Message(), default);

        var auth = handler.ActivityRequest.Headers.Authorization!;
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal("minted-token", auth.Parameter);
    }

    [Fact]
    public async Task TheMessageIsSentAsAMessageActivity()
    {
        var (provider, handler) = Build();

        await provider.SendAsync(Channel(), "secret", Message(text: "the reply"), default);

        var body = TestJson.Element(handler.ActivityBody);
        Assert.Equal("message", body.GetProperty("type").GetString());
        Assert.Equal("the reply", body.GetProperty("text").GetString());
    }

    // Teams rejects oversized activities, so the text is capped rather than
    // failing the whole send.
    [Fact]
    public async Task OversizedTextIsTruncatedToTheTeamsLimit()
    {
        var (provider, handler) = Build();

        await provider.SendAsync(Channel(), "secret", Message(text: new string('x', 40_000)), default);

        var text = TestJson.Element(handler.ActivityBody).GetProperty("text").GetString()!;
        Assert.Equal(28_000, text.Length);
    }

    [Fact]
    public async Task NormalSizedTextIsNotTruncated()
    {
        var (provider, handler) = Build();

        await provider.SendAsync(Channel(), "secret", Message(text: "short"), default);

        Assert.Equal("short", TestJson.Element(handler.ActivityBody).GetProperty("text").GetString());
    }

    // A rejected activity must surface as a failure so the worker's retry
    // logic sees it.
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FailedActivityPostThrowsWithTheStatus(HttpStatusCode status)
    {
        var (provider, handler) = Build();
        handler.SendStatus = status;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendAsync(Channel(), "secret", Message(), default));

        Assert.Contains($"teams reply failed: {(int)status}", ex.Message);
    }

    [Fact]
    public async Task SuccessfulSendCompletesQuietly()
    {
        var (provider, handler) = Build();
        handler.SendStatus = HttpStatusCode.Created;

        await provider.SendAsync(Channel(), "secret", Message(), default);

        Assert.Equal(2, handler.Requests.Count);   // token + activity
    }

    // Teams renders the agent's markdown only when the activity says so.
    [Fact]
    public async Task TheActivityDeclaresMarkdown()
    {
        var (provider, handler) = Build();

        await provider.SendAsync(Channel(), "secret", Message(text: "**bold**"), default);

        Assert.Equal("markdown", TestJson.Element(handler.ActivityBody).GetProperty("textFormat").GetString());
    }

    // ─── AAD token caching ──────────────────────────────────────────────
    //
    // Client-credentials tokens live about an hour. Minting one per reply is a
    // round trip of latency on every message and a throttling risk on a busy
    // channel, so a token with a known lifetime is reused.

    private static string TokenBodyWithLifetime(int seconds, string token = "aad-token-123") =>
        $$"""{"access_token":"{{token}}","expires_in":{{seconds}}}""";

    [Fact]
    public async Task ATokenWithALifetimeIsReusedForTheNextSend()
    {
        var (provider, handler) = Build();
        handler.TokenBody = TokenBodyWithLifetime(3600);
        var channel = Channel();

        await provider.SendAsync(channel, "secret", Message(), default);
        await provider.SendAsync(channel, "secret", Message(), default);

        Assert.Equal(1, handler.Requests.Count(r =>
            r.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.Ordinal)));
        Assert.Equal(3, handler.Requests.Count);   // 1 token + 2 activities
    }

    // A response that never says how long the token is good for is used once
    // rather than cached on a guess.
    [Fact]
    public async Task ATokenWithoutALifetimeIsNotCached()
    {
        var (provider, handler) = Build();
        handler.TokenBody = """{"access_token":"aad-token-123"}""";
        var channel = Channel();

        await provider.SendAsync(channel, "secret", Message(), default);
        await provider.SendAsync(channel, "secret", Message(), default);

        Assert.Equal(2, handler.Requests.Count(r =>
            r.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.Ordinal)));
    }

    // A token about to expire is not worth reusing — the in-flight reply would
    // race the clock.
    [Fact]
    public async Task ATokenExpiringWithinTheSkewIsNotCached()
    {
        var (provider, handler) = Build();
        handler.TokenBody = TokenBodyWithLifetime(30);
        var channel = Channel();

        await provider.SendAsync(channel, "secret", Message(), default);
        await provider.SendAsync(channel, "secret", Message(), default);

        Assert.Equal(2, handler.Requests.Count(r =>
            r.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.Ordinal)));
    }

    // Rotating the app secret must not keep replaying a token minted from the
    // old one.
    [Fact]
    public async Task RotatingTheSecretMissesTheCache()
    {
        var (provider, handler) = Build();
        handler.TokenBody = TokenBodyWithLifetime(3600);
        var channel = Channel();

        await provider.SendAsync(channel, "old-secret", Message(), default);
        await provider.SendAsync(channel, "new-secret", Message(), default);

        Assert.Equal(2, handler.Requests.Count(r =>
            r.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.Ordinal)));
    }

    // Two channels share the singleton provider; neither may use the other's
    // credential.
    [Fact]
    public async Task EachChannelGetsItsOwnCachedToken()
    {
        var (provider, handler) = Build();
        handler.TokenBody = TokenBodyWithLifetime(3600);

        await provider.SendAsync(Channel(), "secret", Message(), default);
        await provider.SendAsync(Channel(), "secret", Message(), default);

        Assert.Equal(2, handler.Requests.Count(r =>
            r.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.Ordinal)));
    }

    // A credential the Connector rejects is dropped, so the retry mints a fresh
    // one instead of replaying a revoked token.
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARejectedTokenIsEvictedSoTheRetryMintsAFreshOne(HttpStatusCode status)
    {
        var (provider, handler) = Build();
        handler.TokenBody = TokenBodyWithLifetime(3600);
        handler.SendStatus = status;
        var channel = Channel();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendAsync(channel, "secret", Message(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendAsync(channel, "secret", Message(), default));

        Assert.Equal(2, handler.Requests.Count(r =>
            r.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.Ordinal)));
    }

    // A 500 is transient and says nothing about the credential, so the cached
    // token survives.
    [Fact]
    public async Task ATransientSendFailureKeepsTheCachedToken()
    {
        var (provider, handler) = Build();
        handler.TokenBody = TokenBodyWithLifetime(3600);
        handler.SendStatus = HttpStatusCode.InternalServerError;
        var channel = Channel();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendAsync(channel, "secret", Message(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendAsync(channel, "secret", Message(), default));

        Assert.Equal(1, handler.Requests.Count(r =>
            r.RequestUri!.Host.Contains("login.microsoftonline.com", StringComparison.Ordinal)));
    }

    // ─── verification (the pre-JWT branches) ────────────────────────────

    // A non-POST (Teams' GET probe) needs no bearer.
    [Fact]
    public async Task NonPostRequestsAreVerifiedWithoutAToken()
    {
        var (provider, _) = Build();

        var result = await provider.VerifyAsync(
            Channel(), new MessagingHttpRequest { Method = "GET", Body = Array.Empty<byte>() }, null, default);

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    [Fact]
    public async Task MissingBearerIsRejectedByDefault()
    {
        var (provider, _) = Build();

        var result = await provider.VerifyAsync(
            Channel(), new MessagingHttpRequest { Method = "POST", Body = Array.Empty<byte>() }, null, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Equal(401, result.RejectStatusCode);
        Assert.Contains("missing bearer", result.RejectReason);
    }

    // The explicit opt-out lets an operator accept unsigned deliveries.
    [Fact]
    public async Task MissingBearerIsAcceptedWhenUnsignedIsAllowed()
    {
        var (provider, _) = Build();
        var channel = Channel();
        channel.AllowUnsigned = true;

        var result = await provider.VerifyAsync(
            channel, new MessagingHttpRequest { Method = "POST", Body = Array.Empty<byte>() }, null, default);

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    // The audience is the channel's app id; without one the token cannot be
    // validated at all.
    [Fact]
    public async Task BearerWithoutAConfiguredAppIdIsRejected()
    {
        var (provider, _) = Build();
        var request = new MessagingHttpRequest
        {
            Method = "POST",
            Body = Array.Empty<byte>(),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer some.jwt.token",
            },
        };

        var result = await provider.VerifyAsync(Channel(appId: null), request, null, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Contains("no app_id", result.RejectReason);
    }

    [Fact]
    public void ProviderTypeIsTeams()
    {
        var (provider, _) = Build();

        Assert.Equal(MessagingChannel.ProviderTeams, provider.Provider);
    }
}
