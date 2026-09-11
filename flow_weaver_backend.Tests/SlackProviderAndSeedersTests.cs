using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Messaging.Providers;
using flow_weaver_backend.Services.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The Slack transport plus two pieces of startup/agent plumbing.
//
// Slack's webhook is public and unauthenticated at the transport level, so the
// signature check is the only thing standing between a stranger and the agent.
// Every refusal here is a security boundary: no secret, stale timestamp, wrong
// signature. The parse half matters for a different reason — echoing the bot's
// own messages back would loop the conversation forever.
public class SlackProviderAndSeedersTests
{
    private const string Secret = "slack-signing-secret";

    private static SlackProvider Slack(FakeHttpMessageHandler? http = null)
        => new(new FakeHttpClientFactory(http ?? new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""")),
               NullLogger<SlackProvider>.Instance);

    private static MessagingChannel Channel(bool allowUnsigned = false)
        => new()
        {
            MessagingChannelId = Guid.NewGuid(),
            Name = "ops",
            Provider = MessagingChannel.ProviderSlack,
            AllowUnsigned = allowUnsigned,
            Enabled = true,
            IsActive = true,
        };

    private static string Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string Sign(string timestamp, string body)
        => "v0=" + Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret),
            Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}"))).ToLowerInvariant();

    private static MessagingHttpRequest Post(
        string body, string? timestamp = null, string? signature = null, bool sign = true)
    {
        var ts = timestamp ?? Now();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sign)
        {
            headers["X-Slack-Request-Timestamp"] = ts;
            headers["X-Slack-Signature"] = signature ?? Sign(ts, body);
        }
        return new MessagingHttpRequest
        {
            Method = "POST",
            Body = Encoding.UTF8.GetBytes(body),
            Headers = headers,
        };
    }

    // ─── Verification ───────────────────────────────────────────────────

    [Fact]
    public async Task Verify_AValidSignatureIsAccepted()
    {
        var result = await Slack().VerifyAsync(Channel(), Post("{}"), Secret, default);

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    // Default posture is deny: without a configured secret the endpoint is
    // wide open, so it refuses unless the admin explicitly opted in.
    [Fact]
    public async Task Verify_NoSecretAndNoOptInIsRejected()
    {
        var result = await Slack().VerifyAsync(Channel(), Post("{}", sign: false), null, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Equal(401, result.RejectStatusCode);
        Assert.Contains("allow_unsigned", result.RejectReason);
    }

    [Fact]
    public async Task Verify_AllowUnsignedIsTheAdminsExplicitOptOut()
    {
        var result = await Slack().VerifyAsync(
            Channel(allowUnsigned: true), Post("{}", sign: false), null, default);

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    [Fact]
    public async Task Verify_MissingSignatureHeadersAreRejected()
    {
        var result = await Slack().VerifyAsync(Channel(), Post("{}", sign: false), Secret, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Contains("missing slack signature headers", result.RejectReason);
    }

    [Fact]
    public async Task Verify_AWrongSignatureIsRejected()
    {
        var result = await Slack().VerifyAsync(
            Channel(), Post("{}", signature: "v0=deadbeef"), Secret, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Contains("signature mismatch", result.RejectReason);
    }

    // Replay protection: a correctly-signed request captured hours ago must
    // not be replayable.
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task Verify_AStaleTimestampIsRejected(int offsetSeconds)
    {
        var ts = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + offsetSeconds)
            .ToString(CultureInfo.InvariantCulture);

        var result = await Slack().VerifyAsync(Channel(), Post("{}", timestamp: ts), Secret, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Contains("stale request timestamp", result.RejectReason);
    }

    [Fact]
    public async Task Verify_ANonNumericTimestampIsRejected()
    {
        var result = await Slack().VerifyAsync(
            Channel(), Post("{}", timestamp: "not-a-number"), Secret, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
    }

    // Slack's one-time URL-verification handshake is only answered for an
    // authentic request — otherwise anyone could confirm the endpoint.
    [Fact]
    public async Task Verify_AnAuthenticUrlVerificationIsAnswered()
    {
        var body = """{"type":"url_verification","challenge":"abc123"}""";

        var result = await Slack().VerifyAsync(Channel(), Post(body), Secret, default);

        Assert.Equal(WebhookVerifyOutcome.Challenge, result.Outcome);
        Assert.Equal("abc123", result.ChallengeBody);
    }

    [Fact]
    public async Task Verify_AnUnsignedUrlVerificationIsNotAnswered()
    {
        var body = """{"type":"url_verification","challenge":"abc123"}""";

        var result = await Slack().VerifyAsync(Channel(), Post(body, sign: false), Secret, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
    }

    // A non-POST (Slack never uses one, but proxies probe with GET) sails
    // through — there is no body to authenticate.
    [Fact]
    public async Task Verify_ANonPostRequestNeedsNoSignature()
    {
        var request = new MessagingHttpRequest { Method = "GET", Body = Array.Empty<byte>() };

        var result = await Slack().VerifyAsync(Channel(), request, Secret, default);

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    // ─── Inbound parsing ────────────────────────────────────────────────

    private static string EventCallback(
        string type = "message", string user = "U1", string channel = "C1",
        string ts = "1700000000.1", string? threadTs = null, string text = "hola",
        string? subtype = null, bool bot = false, string eventId = "Ev1")
    {
        var ev = new Dictionary<string, object?>
        {
            ["type"] = type, ["user"] = user, ["channel"] = channel, ["ts"] = ts, ["text"] = text,
        };
        if (threadTs is not null) ev["thread_ts"] = threadTs;
        if (subtype is not null) ev["subtype"] = subtype;
        if (bot) ev["bot_id"] = "B1";
        return JsonSerializer.Serialize(new
        {
            type = "event_callback",
            event_id = eventId,
            team_id = "T1",
            @event = ev,
        });
    }

    private static InboundMessage? Parse(string body)
        => Slack().ParseInbound(Channel(), new MessagingHttpRequest
        {
            Method = "POST",
            Body = Encoding.UTF8.GetBytes(body),
        });

    [Fact]
    public void Parse_ReadsAUserMessage()
    {
        var msg = Parse(EventCallback());

        Assert.NotNull(msg);
        Assert.Equal("Ev1", msg!.ProviderEventId);
        Assert.Equal("T1", msg.ExternalWorkspaceId);
        Assert.Equal("U1", msg.ExternalUserId);
        Assert.Equal("hola", msg.Text);
        Assert.Equal("message", msg.EventKind);
    }

    // A top-level message / DM is one conversation keyed on the channel, so
    // replies land in the main flow rather than nested in a thread.
    [Fact]
    public void Parse_ATopLevelMessageKeysTheThreadOnTheChannelAlone()
    {
        Assert.Equal("C1", Parse(EventCallback())!.ExternalThreadId);
    }

    [Fact]
    public void Parse_AThreadedMessageKeepsTheThreadTimestamp()
    {
        Assert.Equal("C1:1699999999.9",
            Parse(EventCallback(threadTs: "1699999999.9"))!.ExternalThreadId);
    }

    // The bot's own messages would loop the conversation forever.
    [Fact]
    public void Parse_TheBotsOwnMessagesAreDropped()
    {
        Assert.Null(Parse(EventCallback(bot: true)));
    }

    [Theory]
    [InlineData("message_changed")]
    [InlineData("message_deleted")]
    [InlineData("bot_message")]
    public void Parse_SystemSubtypesAreDropped(string subtype)
    {
        Assert.Null(Parse(EventCallback(subtype: subtype)));
    }

    // A genuine user subtype (uploading a file) is still a message.
    [Fact]
    public void Parse_GenuineUserSubtypesSurvive()
    {
        Assert.NotNull(Parse(EventCallback(subtype: "file_share")));
    }

    [Fact]
    public void Parse_AppMentionsAreAccepted()
    {
        Assert.Equal("app_mention", Parse(EventCallback(type: "app_mention"))!.EventKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"type":"url_verification"}""")]
    [InlineData("""{"type":"event_callback"}""")]
    public void Parse_NonMessagePayloadsYieldNothing(string body)
    {
        Assert.Null(Parse(body));
    }

    [Theory]
    [InlineData("", "C1", "1700000000.1")]
    [InlineData("U1", "", "1700000000.1")]
    [InlineData("U1", "C1", "")]
    public void Parse_AnIncompleteEventIsDropped(string user, string channel, string ts)
    {
        Assert.Null(Parse(EventCallback(user: user, channel: channel, ts: ts)));
    }

    // Without an event_id Slack gives us nothing to dedupe on, so one is
    // synthesised from (channel, ts) — the pair is unique per message.
    [Fact]
    public void Parse_AMissingEventIdFallsBackToChannelAndTimestamp()
    {
        Assert.Equal("C1:1700000000.1", Parse(EventCallback(eventId: ""))!.ProviderEventId);
    }

    // ─── Sending ────────────────────────────────────────────────────────

    private static OutboundMessage Outbound(string threadId, string text = "listo")
        => new() { ExternalThreadId = threadId, Text = text };

    [Fact]
    public async Task Send_PostsToChatPostMessageWithTheBotToken()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""");

        await Slack(http).SendAsync(Channel(), "xoxb-token", Outbound("C1"), default);

        var request = http.Requests[^1];
        Assert.Equal("https://slack.com/api/chat.postMessage", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("xoxb-token", request.Headers.Authorization.Parameter);
    }

    // A top-level reply must NOT carry thread_ts, or it nests under the
    // original message instead of continuing the flow.
    [Fact]
    public async Task Send_ATopLevelReplyCarriesNoThreadTs()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""");

        await Slack(http).SendAsync(Channel(), "xoxb", Outbound("C1"), default);

        var body = http.RequestBodies[^1];
        Assert.Contains("\"channel\":\"C1\"", body);
        Assert.DoesNotContain("thread_ts", body);
    }

    [Fact]
    public async Task Send_AThreadedReplyCarriesTheThreadTs()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""");

        await Slack(http).SendAsync(Channel(), "xoxb", Outbound("C1:1699999999.9"), default);

        var body = http.RequestBodies[^1];
        Assert.Contains("\"channel\":\"C1\"", body);
        Assert.Contains("\"thread_ts\":\"1699999999.9\"", body);
    }

    [Fact]
    public async Task Send_WithoutABotTokenIsRefusedBeforeAnyCall()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Slack(http).SendAsync(Channel(), null, Outbound("C1"), default));

        Assert.Empty(http.Requests);
    }

    // Slack answers HTTP 200 with {ok:false} on logical failures — treating
    // that as success would silently drop the agent's reply.
    [Fact]
    public async Task Send_A200WithOkFalseIsStillAFailure()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":false,"error":"channel_not_found"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Slack(http).SendAsync(Channel(), "xoxb", Outbound("C1"), default));
    }

    [Fact]
    public async Task Send_AnHttpErrorIsAFailure()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Slack(http).SendAsync(Channel(), "xoxb", Outbound("C1"), default));
    }

    // Slack's text field has a hard 40k limit; a longer agent answer has to be
    // truncated rather than rejected by the API.
    [Fact]
    public async Task Send_TheTextIsTruncatedToSlacksLimit()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""");

        await Slack(http).SendAsync(Channel(), "xoxb", Outbound("C1", new string('x', 50_000)), default);

        using var doc = JsonDocument.Parse(http.RequestBodies[^1]);
        Assert.True(doc.RootElement.GetProperty("text").GetString()!.Length <= 40_000);
    }

    // ─── PolicyDefaultsSeeder ───────────────────────────────────────────

    private static ServiceProvider SeederProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task PolicySeeder_CreatesTheDefaultGate()
    {
        var dbName = Guid.NewGuid().ToString();
        using var sp = SeederProvider(dbName);
        await PolicyDefaultsSeeder.SeedAsync(
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);

        using var check = sp.CreateScope();
        var policy = Assert.Single(
            check.ServiceProvider.GetRequiredService<AppDbContext>().Policies.ToList());
        Assert.Equal(PolicyDefaultsSeeder.DefaultPromotionGateName, policy.Name);
        Assert.True(policy.Enabled);
        Assert.Equal("gate", policy.Rule.GetProperty("action").GetString());
        Assert.Equal("qa", policy.Rule.GetProperty("from").GetString());
        Assert.Equal("production", policy.Rule.GetProperty("to").GetString());
    }

    // Runs on every startup, so it has to be a no-op the second time.
    [Fact]
    public async Task PolicySeeder_IsIdempotent()
    {
        var dbName = Guid.NewGuid().ToString();
        using var sp = SeederProvider(dbName);
        var factory = sp.GetRequiredService<IServiceScopeFactory>();
        await PolicyDefaultsSeeder.SeedAsync(factory, NullLogger.Instance);
        await PolicyDefaultsSeeder.SeedAsync(factory, NullLogger.Instance);

        using var check = sp.CreateScope();
        Assert.Single(check.ServiceProvider.GetRequiredService<AppDbContext>().Policies.ToList());
    }

    // An admin who edited or removed the gate must not have it silently
    // restored on the next restart.
    [Fact]
    public async Task PolicySeeder_DoesNotResurrectAnAdminEditedGate()
    {
        var dbName = Guid.NewGuid().ToString();
        using var sp = SeederProvider(dbName);
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Policies.Add(new Policy
            {
                PolicyId = Guid.NewGuid(),
                Name = PolicyDefaultsSeeder.DefaultPromotionGateName,
                Rule = TestJson.Element("""{"action":"allow"}"""),
                Enabled = false,
                IsActive = true,
            });
            db.SaveChanges();
        }

        await PolicyDefaultsSeeder.SeedAsync(
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);

        using var check = sp.CreateScope();
        var policy = Assert.Single(check.ServiceProvider.GetRequiredService<AppDbContext>().Policies.ToList());
        Assert.False(policy.Enabled);
        Assert.Equal("allow", policy.Rule.GetProperty("action").GetString());
    }

    // ─── AgentScratchRepository ─────────────────────────────────────────

    [Fact]
    public async Task Scratch_UpsertInsertsThenUpdatesTheSameKey()
    {
        using var db = TestDb.NewContext();
        var repo = new AgentScratchRepository(db);
        var conversation = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var first = await repo.UpsertAsync(conversation, "plan", TestJson.Element("""{"v":1}"""), null, "first", now);
        var second = await repo.UpsertAsync(conversation, "plan", TestJson.Element("""{"v":2}"""), null, null, now.AddMinutes(1));

        Assert.False(first.Raced);
        Assert.False(second.Raced);
        var row = Assert.Single(db.AgentScratches);
        Assert.Equal(2, row.Value.GetProperty("v").GetInt32());
        // A null note keeps the previous one rather than clearing it.
        Assert.Equal("first", row.Note);
    }

    [Fact]
    public async Task Scratch_KeysAreScopedPerConversation()
    {
        using var db = TestDb.NewContext();
        var repo = new AgentScratchRepository(db);
        var now = DateTime.UtcNow;

        await repo.UpsertAsync(Guid.NewGuid(), "plan", TestJson.Element("1"), null, null, now);
        await repo.UpsertAsync(Guid.NewGuid(), "plan", TestJson.Element("2"), null, null, now);

        Assert.Equal(2, db.AgentScratches.Count());
    }

    [Fact]
    public async Task Scratch_ReadReturnsNullForAnUnknownKey()
    {
        using var db = TestDb.NewContext();
        var repo = new AgentScratchRepository(db);

        Assert.Null(await repo.ReadValueAsync(Guid.NewGuid(), "nope"));
    }

    [Fact]
    public async Task Scratch_ListReturnsTheConversationsKeysInOrder()
    {
        using var db = TestDb.NewContext();
        var repo = new AgentScratchRepository(db);
        var conversation = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await repo.UpsertAsync(conversation, "zeta", TestJson.Element("1"), null, null, now);
        await repo.UpsertAsync(conversation, "alpha", TestJson.Element("2"), null, null, now);

        var rows = await repo.ListActiveAsync(conversation);

        Assert.Equal(new[] { "alpha", "zeta" }, rows.Select(r => r.Key));
    }

    // A soft-deleted entry is gone from the agent's view, and upserting the
    // same key afterwards starts a fresh row rather than resurrecting it.
    [Fact]
    public async Task Scratch_SoftDeletedEntriesAreInvisible()
    {
        using var db = TestDb.NewContext();
        var repo = new AgentScratchRepository(db);
        var conversation = Guid.NewGuid();
        await repo.UpsertAsync(conversation, "plan", TestJson.Element("1"), null, null, DateTime.UtcNow);
        db.AgentScratches.Single().IsActive = false;
        db.SaveChanges();

        Assert.Null(await repo.ReadValueAsync(conversation, "plan"));
        Assert.Empty(await repo.ListActiveAsync(conversation));
    }

    [Fact]
    public async Task Scratch_TheWorkflowPlanLinkIsKeptWhenNotResupplied()
    {
        using var db = TestDb.NewContext();
        var repo = new AgentScratchRepository(db);
        var conversation = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await repo.UpsertAsync(conversation, "plan", TestJson.Element("1"), planId, null, now);
        await repo.UpsertAsync(conversation, "plan", TestJson.Element("2"), null, null, now);

        Assert.Equal(planId, db.AgentScratches.Single().WorkflowPlanId);
    }
}
