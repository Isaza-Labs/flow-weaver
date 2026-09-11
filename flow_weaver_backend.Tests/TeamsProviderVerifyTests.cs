using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Messaging.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace flow_weaver_backend.Tests;

// The inbound half of the Teams channel. A Bot Framework activity is trusted
// only because of the Bearer JWT it carries, and the webhook endpoint is
// anonymous, so this is the channel's entire authentication boundary.
//
// The check that earns its own file is the `serviceurl` claim (requirement 7 of
// the Bot Connector auth spec): the reply carries an AAD bearer token to
// whatever serviceUrl the activity named, so a token that authorises a
// *different* endpoint must not be allowed to point us — and our credential —
// somewhere else.
public class TeamsProviderVerifyTests
{
    private const string Issuer = "https://api.botframework.com";
    private const string AppId = "app-123";
    private const string ServiceUrl = "https://smba.trafficmanager.net/amer/";

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Stands in for the Bot Framework's public metadata endpoint, handing back
    // the key the test signed with.
    private sealed class StubConfigManager : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly OpenIdConnectConfiguration _config;
        public StubConfigManager(SecurityKey key)
        {
            _config = new OpenIdConnectConfiguration { Issuer = Issuer };
            _config.SigningKeys.Add(key);
        }
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
            => Task.FromResult(_config);
        public void RequestRefresh() { }
    }

    private static readonly RSA SigningRsa = RSA.Create(2048);
    private static readonly RsaSecurityKey SigningKey = new(SigningRsa) { KeyId = "test-key" };

    private static TeamsProvider Provider() => new(
        new StubFactory(), NullLogger<TeamsProvider>.Instance, new StubConfigManager(SigningKey));

    private static string Token(
        string issuer = Issuer, string audience = AppId, string? serviceUrl = ServiceUrl,
        int lifetimeMinutes = 10)
    {
        var claims = new Dictionary<string, object>();
        if (serviceUrl is not null) claims["serviceurl"] = serviceUrl;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(lifetimeMinutes),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private static MessagingChannel Channel(string? appId = AppId, bool allowUnsigned = false)
    {
        var config = new Dictionary<string, string>();
        if (appId is not null) config["app_id"] = appId;
        return new MessagingChannel
        {
            MessagingChannelId = Guid.NewGuid(),
            Provider = MessagingChannel.ProviderTeams,
            Name = "teams",
            ExternalConfig = TestJson.Element(JsonSerializer.Serialize(config)),
            AllowUnsigned = allowUnsigned,
            IsActive = true,
        };
    }

    private static string ActivityBody(string? serviceUrl = ServiceUrl) => JsonSerializer.Serialize(new
    {
        type = "message",
        id = "a1",
        text = "hi",
        serviceUrl,
        conversation = new { id = "19:conv" },
    });

    private static MessagingHttpRequest Request(string? token, string? body = null) => new()
    {
        Method = "POST",
        Body = Encoding.UTF8.GetBytes(body ?? ActivityBody()),
        Headers = token is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {token}",
            },
    };

    private static Task<WebhookVerifyResult> Verify(
        MessagingChannel? channel = null, MessagingHttpRequest? request = null)
        => Provider().VerifyAsync(channel ?? Channel(), request ?? Request(Token()), null, default);

    // ─── the happy path ─────────────────────────────────────────────────

    [Fact]
    public async Task AWellFormedConnectorTokenIsAccepted()
    {
        var result = await Verify();

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    // ─── standard JWT checks ────────────────────────────────────────────

    // The audience is what ties the token to *this* bot; without it any valid
    // Bot Framework token would open the channel.
    [Fact]
    public async Task ATokenMintedForAnotherBotIsRejected()
    {
        var result = await Verify(request: Request(Token(audience: "someone-elses-app")));

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Equal(401, result.RejectStatusCode);
    }

    [Fact]
    public async Task ATokenFromAnotherIssuerIsRejected()
    {
        var result = await Verify(request: Request(Token(issuer: "https://evil.example")));

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task AnExpiredTokenIsRejected()
    {
        // Beyond the handler's default 5-minute clock skew.
        var result = await Verify(request: Request(Token(lifetimeMinutes: -30)));

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task ATokenSignedWithAnotherKeyIsRejected()
    {
        using var other = RSA.Create(2048);
        var foreign = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = AppId,
            Claims = new Dictionary<string, object> { ["serviceurl"] = ServiceUrl },
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(other) { KeyId = "other" }, SecurityAlgorithms.RsaSha256),
        });

        var result = await Verify(request: Request(foreign));

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task GarbageInTheAuthorizationHeaderIsRejectedWithoutThrowing()
    {
        var result = await Verify(request: Request("not-a-jwt"));

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Equal(401, result.RejectStatusCode);
    }

    // ─── the serviceurl claim ───────────────────────────────────────────

    // The whole point: an otherwise valid token cannot redirect our replies.
    [Fact]
    public async Task ATokenWhoseServiceUrlDisagreesWithTheActivityIsRejected()
    {
        var request = Request(Token(serviceUrl: "https://smba.trafficmanager.net/emea/"));

        var result = await Verify(request: request);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Contains("serviceurl", result.RejectReason);
    }

    [Fact]
    public async Task ATokenWithNoServiceUrlClaimIsRejected()
    {
        var result = await Verify(request: Request(Token(serviceUrl: null)));

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Contains("no serviceurl claim", result.RejectReason);
    }

    [Fact]
    public async Task AnActivityWithNoServiceUrlIsRejected()
    {
        var body = JsonSerializer.Serialize(new { type = "message", id = "a1" });

        var result = await Verify(request: Request(Token(), body));

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Contains("no serviceUrl", result.RejectReason);
    }

    [Fact]
    public async Task AnUnparseableBodyIsRejected()
    {
        var result = await Verify(request: Request(Token(), "}not json{"));

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
    }

    // A trailing slash is a formatting difference, not a different endpoint.
    [Theory]
    [InlineData("https://smba.trafficmanager.net/amer", "https://smba.trafficmanager.net/amer/")]
    [InlineData("https://smba.trafficmanager.net/amer/", "https://smba.trafficmanager.net/amer")]
    [InlineData("https://SMBA.trafficmanager.net/amer/", "https://smba.trafficmanager.net/amer/")]
    public async Task TrailingSlashAndCasingDifferencesStillMatch(string claimed, string activity)
    {
        var result = await Verify(request: Request(Token(serviceUrl: claimed), ActivityBody(activity)));

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    // ─── the pre-JWT branches ───────────────────────────────────────────

    [Fact]
    public async Task AMissingBearerIsRejectedByDefault()
    {
        var result = await Verify(request: Request(null));

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Contains("missing bearer", result.RejectReason);
    }

    // allow_unsigned is the documented testing escape hatch; it short-circuits
    // before any of the checks above.
    [Fact]
    public async Task AllowUnsignedSkipsVerificationEntirely()
    {
        var result = await Verify(Channel(allowUnsigned: true), Request(null));

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    // …but only when there is no token at all. A present-but-bogus token is
    // still refused, so an attacker cannot downgrade a signed channel.
    [Fact]
    public async Task AllowUnsignedDoesNotExcuseABadToken()
    {
        var result = await Verify(
            Channel(allowUnsigned: true), Request(Token(audience: "someone-elses-app")));

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task NonPostRequestsNeedNoToken()
    {
        var result = await Provider().VerifyAsync(
            Channel(), new MessagingHttpRequest { Method = "GET", Body = Array.Empty<byte>() }, null, default);

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }
}
