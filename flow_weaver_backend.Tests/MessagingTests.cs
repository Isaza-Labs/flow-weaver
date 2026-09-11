using System.Text;
using System.Text.Json;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Messaging.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

public class MessagingRolesTests
{
    [Theory]
    // user role, channel max role → effective
    [InlineData("viewer", null, "viewer")]
    [InlineData("admin", null, "admin")]
    [InlineData("admin", "operator", "operator")]   // ceiling caps down
    [InlineData("operator", "admin", "operator")]   // user already below ceiling
    [InlineData("viewer", "operator", "viewer")]    // never raised to the ceiling
    [InlineData("admin", "viewer", "viewer")]       // hard cap
    public void Effective_never_exceeds_either_bound(string userRole, string? maxRole, string expected)
    {
        Assert.Equal(expected, MessagingRoles.Effective(userRole, maxRole));
    }
}

public class TelegramProviderTests
{
    private static TelegramProvider NewProvider() =>
        new(new StubHttpClientFactory(), NullLogger<TelegramProvider>.Instance);

    private static MessagingHttpRequest Post(byte[] body, params (string, string)[] headers)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers) dict[k] = v;
        return new MessagingHttpRequest { Method = "POST", Body = body, Headers = dict };
    }

    [Fact]
    public void ParseInbound_extracts_message_fields()
    {
        var body = Encoding.UTF8.GetBytes(
            "{\"update_id\":42,\"message\":{\"text\":\"hi\",\"from\":{\"id\":111,\"username\":\"bob\"},\"chat\":{\"id\":222}}}");
        var msg = NewProvider().ParseInbound(new MessagingChannel(), Post(body));

        Assert.NotNull(msg);
        Assert.Equal("42", msg!.ProviderEventId);
        Assert.Equal("222", msg.ExternalThreadId);
        Assert.Equal("111", msg.ExternalUserId);
        Assert.Equal("hi", msg.Text);
        Assert.Equal(string.Empty, msg.ExternalWorkspaceId);
    }

    [Fact]
    public void ParseInbound_ignores_non_text_and_edited()
    {
        var edited = Encoding.UTF8.GetBytes("{\"update_id\":1,\"edited_message\":{\"text\":\"x\"}}");
        Assert.Null(NewProvider().ParseInbound(new MessagingChannel(), Post(edited)));

        var noText = Encoding.UTF8.GetBytes("{\"update_id\":2,\"message\":{\"from\":{\"id\":1},\"chat\":{\"id\":2}}}");
        Assert.Null(NewProvider().ParseInbound(new MessagingChannel(), Post(noText)));
    }

    [Fact]
    public void ParseInbound_skips_bot_authored_messages()
    {
        // Prevents reply loops in groups with other bots.
        var body = Encoding.UTF8.GetBytes(
            "{\"update_id\":7,\"message\":{\"text\":\"hi\",\"from\":{\"id\":1,\"is_bot\":true},\"chat\":{\"id\":2}}}");
        Assert.Null(NewProvider().ParseInbound(new MessagingChannel(), Post(body)));
    }

    [Fact]
    public async Task Verify_matches_secret_token()
    {
        var channel = new MessagingChannel { Provider = MessagingChannel.ProviderTelegram };
        var req = Post(Array.Empty<byte>(), ("X-Telegram-Bot-Api-Secret-Token", "s3cret"));

        var ok = await NewProvider().VerifyAsync(channel, req, "s3cret", CancellationToken.None);
        Assert.Equal(WebhookVerifyOutcome.Verified, ok.Outcome);

        var bad = await NewProvider().VerifyAsync(channel, req, "other", CancellationToken.None);
        Assert.Equal(WebhookVerifyOutcome.Rejected, bad.Outcome);
    }

    [Fact]
    public async Task Verify_requires_secret_unless_allow_unsigned()
    {
        var req = Post(Array.Empty<byte>());

        var strict = new MessagingChannel { Provider = MessagingChannel.ProviderTelegram, AllowUnsigned = false };
        Assert.Equal(WebhookVerifyOutcome.Rejected,
            (await NewProvider().VerifyAsync(strict, req, null, CancellationToken.None)).Outcome);

        var lax = new MessagingChannel { Provider = MessagingChannel.ProviderTelegram, AllowUnsigned = true };
        Assert.Equal(WebhookVerifyOutcome.Verified,
            (await NewProvider().VerifyAsync(lax, req, null, CancellationToken.None)).Outcome);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

public class SlackProviderTests
{
    private static SlackProvider NewProvider() =>
        new(new StubFactory(), NullLogger<SlackProvider>.Instance);

    private static string Hmac(string secret, string message)
    {
        using var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return System.Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
    }

    private static MessagingHttpRequest Signed(string secret, string body)
    {
        var ts = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sig = "v0=" + Hmac(secret, $"v0:{ts}:{body}");
        return new MessagingHttpRequest
        {
            Method = "POST",
            Body = Encoding.UTF8.GetBytes(body),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Slack-Request-Timestamp"] = ts,
                ["X-Slack-Signature"] = sig,
            },
        };
    }

    [Fact]
    public async Task Verify_accepts_valid_signature_and_answers_challenge()
    {
        const string secret = "shhh";
        var channel = new MessagingChannel { Provider = MessagingChannel.ProviderSlack };

        var verified = await NewProvider().VerifyAsync(
            channel, Signed(secret, "{\"type\":\"event_callback\"}"), secret, CancellationToken.None);
        Assert.Equal(WebhookVerifyOutcome.Verified, verified.Outcome);

        var challenge = await NewProvider().VerifyAsync(
            channel, Signed(secret, "{\"type\":\"url_verification\",\"challenge\":\"abc123\"}"), secret, CancellationToken.None);
        Assert.Equal(WebhookVerifyOutcome.Challenge, challenge.Outcome);
        Assert.Equal("abc123", challenge.ChallengeBody);
    }

    [Fact]
    public async Task Verify_rejects_tampered_signature()
    {
        var req = Signed("shhh", "{\"type\":\"event_callback\"}");
        var result = await NewProvider().VerifyAsync(
            new MessagingChannel { Provider = MessagingChannel.ProviderSlack }, req, "different-secret", CancellationToken.None);
        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public void ParseInbound_top_level_message_uses_channel_only()
    {
        // No thread_ts → the reply must go to the channel/DM directly (not nested).
        var body = "{\"type\":\"event_callback\",\"team_id\":\"T9\",\"event_id\":\"Ev1\","
                   + "\"event\":{\"type\":\"message\",\"user\":\"U1\",\"text\":\"hello\",\"ts\":\"100.1\",\"channel\":\"C7\"}}";
        var msg = NewProvider().ParseInbound(
            new MessagingChannel(), new MessagingHttpRequest { Method = "POST", Body = Encoding.UTF8.GetBytes(body) });

        Assert.NotNull(msg);
        Assert.Equal("Ev1", msg!.ProviderEventId);
        Assert.Equal("T9", msg.ExternalWorkspaceId);
        Assert.Equal("U1", msg.ExternalUserId);
        Assert.Equal("C7", msg.ExternalThreadId);
        Assert.Equal("hello", msg.Text);
    }

    [Fact]
    public void ParseInbound_threaded_message_keeps_thread()
    {
        // thread_ts present → stay in the thread (channel:thread_ts).
        var body = "{\"type\":\"event_callback\",\"event_id\":\"Ev2\","
                   + "\"event\":{\"type\":\"message\",\"user\":\"U1\",\"text\":\"reply\",\"ts\":\"200.2\",\"thread_ts\":\"100.1\",\"channel\":\"C7\"}}";
        var msg = NewProvider().ParseInbound(
            new MessagingChannel(), new MessagingHttpRequest { Method = "POST", Body = Encoding.UTF8.GetBytes(body) });

        Assert.NotNull(msg);
        Assert.Equal("C7:100.1", msg!.ExternalThreadId);
    }

    [Fact]
    public void ParseInbound_skips_bot_messages()
    {
        var body = "{\"type\":\"event_callback\",\"event\":{\"type\":\"message\",\"bot_id\":\"B1\",\"text\":\"x\",\"ts\":\"1\",\"channel\":\"C\"}}";
        Assert.Null(NewProvider().ParseInbound(
            new MessagingChannel(), new MessagingHttpRequest { Method = "POST", Body = Encoding.UTF8.GetBytes(body) }));
    }

    [Fact]
    public void ParseInbound_drops_edits_but_keeps_file_share()
    {
        var changed = "{\"type\":\"event_callback\",\"event\":{\"type\":\"message\",\"subtype\":\"message_changed\","
                      + "\"user\":\"U\",\"text\":\"x\",\"ts\":\"1\",\"channel\":\"C\"}}";
        Assert.Null(NewProvider().ParseInbound(
            new MessagingChannel(), new MessagingHttpRequest { Method = "POST", Body = Encoding.UTF8.GetBytes(changed) }));

        var fileShare = "{\"type\":\"event_callback\",\"event\":{\"type\":\"message\",\"subtype\":\"file_share\","
                        + "\"user\":\"U\",\"text\":\"caption\",\"ts\":\"1\",\"channel\":\"C\"}}";
        var msg = NewProvider().ParseInbound(
            new MessagingChannel(), new MessagingHttpRequest { Method = "POST", Body = Encoding.UTF8.GetBytes(fileShare) });
        Assert.NotNull(msg);
        Assert.Equal("caption", msg!.Text);
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

public class WhatsAppProviderTests
{
    private static WhatsAppProvider NewProvider() =>
        new(new StubFactory(), NullLogger<WhatsAppProvider>.Instance);

    [Fact]
    public async Task Verify_get_challenge_matches_verify_token()
    {
        var channel = new MessagingChannel
        {
            Provider = MessagingChannel.ProviderWhatsApp,
            ExternalConfig = System.Text.Json.JsonSerializer.SerializeToElement(new { verify_token = "vt" }),
        };
        var req = new MessagingHttpRequest
        {
            Method = "GET",
            Body = System.Array.Empty<byte>(),
            Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["hub.mode"] = "subscribe",
                ["hub.verify_token"] = "vt",
                ["hub.challenge"] = "42",
            },
        };
        var r = await NewProvider().VerifyAsync(channel, req, null, CancellationToken.None);
        Assert.Equal(WebhookVerifyOutcome.Challenge, r.Outcome);
        Assert.Equal("42", r.ChallengeBody);
    }

    [Fact]
    public async Task Verify_post_checks_app_secret_signature()
    {
        const string secret = "app-secret";
        var body = Encoding.UTF8.GetBytes("{\"object\":\"whatsapp_business_account\"}");
        using var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = "sha256=" + System.Convert.ToHexString(h.ComputeHash(body)).ToLowerInvariant();

        var req = new MessagingHttpRequest
        {
            Method = "POST",
            Body = body,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Hub-Signature-256"] = sig },
        };
        var ok = await NewProvider().VerifyAsync(
            new MessagingChannel { Provider = MessagingChannel.ProviderWhatsApp }, req, secret, CancellationToken.None);
        Assert.Equal(WebhookVerifyOutcome.Verified, ok.Outcome);
    }

    [Fact]
    public void ParseInbound_extracts_text_message()
    {
        var body = "{\"object\":\"whatsapp_business_account\",\"entry\":[{\"changes\":[{\"value\":{"
                   + "\"metadata\":{\"phone_number_id\":\"PN1\"},"
                   + "\"messages\":[{\"from\":\"15551234\",\"id\":\"wamid.1\",\"type\":\"text\",\"text\":{\"body\":\"hey\"}}]}}]}]}";
        var msg = NewProvider().ParseInbound(
            new MessagingChannel(), new MessagingHttpRequest { Method = "POST", Body = Encoding.UTF8.GetBytes(body) });

        Assert.NotNull(msg);
        Assert.Equal("wamid.1", msg!.ProviderEventId);
        Assert.Equal("15551234", msg.ExternalUserId);
        Assert.Equal("15551234", msg.ExternalThreadId);
        Assert.Equal("PN1", msg.ExternalWorkspaceId);
        Assert.Equal("hey", msg.Text);
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

public class TeamsProviderTests
{
    private static TeamsProvider NewProvider() =>
        new(new StubFactory(), NullLogger<TeamsProvider>.Instance);

    [Fact]
    public void ParseInbound_encodes_serviceurl_and_conversation()
    {
        var body = "{\"type\":\"message\",\"id\":\"a1\",\"text\":\"hi\","
                   + "\"serviceUrl\":\"https://smba.example/teams\","
                   + "\"from\":{\"id\":\"29:x\",\"name\":\"Bob\",\"aadObjectId\":\"aad-1\"},"
                   + "\"conversation\":{\"id\":\"19:conv\"},"
                   + "\"channelData\":{\"tenant\":{\"id\":\"tenant-1\"}}}";
        var msg = NewProvider().ParseInbound(
            new MessagingChannel(), new MessagingHttpRequest { Method = "POST", Body = Encoding.UTF8.GetBytes(body) });

        Assert.NotNull(msg);
        Assert.Equal("a1", msg!.ProviderEventId);
        Assert.Equal("aad-1", msg.ExternalUserId);          // stable AAD identity
        Assert.Equal("tenant-1", msg.ExternalWorkspaceId);
        Assert.Equal("https://smba.example/teams::19:conv", msg.ExternalThreadId);
        Assert.Equal("hi", msg.Text);
        Assert.Equal("Bob", msg.SenderDisplayName);
    }

    // ─── mention markup ─────────────────────────────────────────────────
    //
    // In a channel or group chat Teams only delivers messages that @mention the
    // bot, and that mention travels inside `text` as markup. Left in, the agent
    // reads "<at>FlowWeaver</at> list devices" as the user's prompt.

    private static string Activity(
        string text, string? entities = null, string recipientId = "28:bot-app",
        string fromId = "29:x")
        => "{\"type\":\"message\",\"id\":\"a1\","
           + $"\"text\":{JsonSerializer.Serialize(text)},"
           + "\"serviceUrl\":\"https://smba.example/teams\","
           + $"\"from\":{{\"id\":\"{fromId}\",\"name\":\"Bob\",\"aadObjectId\":\"aad-1\"}},"
           + $"\"recipient\":{{\"id\":\"{recipientId}\",\"name\":\"FlowWeaver\"}},"
           + "\"conversation\":{\"id\":\"19:conv\"}"
           + (entities is null ? string.Empty : $",\"entities\":{entities}")
           + "}";

    private static InboundMessage? Parse(string body) =>
        NewProvider().ParseInbound(
            new MessagingChannel(), new MessagingHttpRequest { Method = "POST", Body = Encoding.UTF8.GetBytes(body) });

    private const string BotMention =
        """[{"type":"mention","text":"<at>FlowWeaver</at>","mentioned":{"id":"28:bot-app","name":"FlowWeaver"}}]""";

    [Fact]
    public void ParseInbound_strips_the_bots_own_mention()
    {
        var msg = Parse(Activity("<at>FlowWeaver</at> list the devices", BotMention));

        Assert.NotNull(msg);
        Assert.Equal("list the devices", msg!.Text);
    }

    // A client that inlines the tag without a matching entity must not leak
    // markup either.
    [Fact]
    public void ParseInbound_strips_mention_markup_without_an_entity()
    {
        var msg = Parse(Activity("<at>FlowWeaver</at> ping"));

        Assert.NotNull(msg);
        Assert.Equal("FlowWeaver ping", msg!.Text);
    }

    // Another user's mention is part of what the user said, so it survives as
    // the plain display name rather than as markup.
    [Fact]
    public void ParseInbound_unwraps_other_peoples_mentions_to_their_name()
    {
        const string entities =
            """[{"type":"mention","text":"<at>FlowWeaver</at>","mentioned":{"id":"28:bot-app","name":"FlowWeaver"}},"""
            + """{"type":"mention","text":"<at>Ana</at>","mentioned":{"id":"29:ana","name":"Ana"}}]""";

        var msg = Parse(Activity("<at>FlowWeaver</at> tell <at>Ana</at> it is done", entities));

        Assert.NotNull(msg);
        Assert.Equal("tell Ana it is done", msg!.Text);
    }

    // Teams pads mentions with &nbsp; and escapes user text; the agent should
    // see characters, not entities.
    [Fact]
    public void ParseInbound_decodes_html_entities()
    {
        var msg = Parse(Activity("<at>FlowWeaver</at>&nbsp;show A &amp; B", BotMention));

        Assert.NotNull(msg);
        Assert.Equal("show A & B", msg!.Text);
    }

    // A bare @mention (or a message that was only an attachment) carries no
    // prompt — running the agent on "" burns a turn to say nothing.
    [Theory]
    [InlineData("<at>FlowWeaver</at>")]
    [InlineData("<at>FlowWeaver</at>   ")]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseInbound_drops_messages_with_no_text_left(string text)
    {
        Assert.Null(Parse(Activity(text, BotMention)));
    }

    // ─── loop guard ─────────────────────────────────────────────────────

    // Teams user ids are "29:…"; "28:…" is a bot. Answering one is how a bot
    // ends up talking to itself.
    [Theory]
    [InlineData("28:bot-app")]
    [InlineData("28:some-other-bot")]
    public void ParseInbound_ignores_activities_authored_by_a_bot(string fromId)
    {
        Assert.Null(Parse(Activity("hello", fromId: fromId)));
    }

    [Fact]
    public void ParseInbound_ignores_non_message_activities()
    {
        const string body = "{\"type\":\"conversationUpdate\",\"id\":\"a1\","
                            + "\"serviceUrl\":\"https://smba.example/teams\","
                            + "\"from\":{\"id\":\"29:x\"},\"conversation\":{\"id\":\"19:conv\"}}";

        Assert.Null(Parse(body));
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
