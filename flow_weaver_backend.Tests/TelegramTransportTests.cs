using System.Net;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Messaging.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The Telegram transport. Its webhook is public and unauthenticated at the
// transport level, so the secret-token header is the only thing standing
// between a stranger and the agent — and the bot-authored-message filter is
// the only thing standing between two bots in a group and an infinite reply
// loop.
public class TelegramTransportTests
{
    private const string Secret = "telegram-secret-token";
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    private static TelegramProvider Telegram(FakeHttpMessageHandler? http = null)
        => new(new FakeHttpClientFactory(http ?? new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""")),
               NullLogger<TelegramProvider>.Instance);

    private static MessagingChannel Channel(bool allowUnsigned = false)
        => new()
        {
            MessagingChannelId = Guid.NewGuid(),
            Name = "ops",
            Provider = MessagingChannel.ProviderTelegram,
            AllowUnsigned = allowUnsigned,
            Enabled = true,
            IsActive = true,
        };

    private static MessagingHttpRequest Post(string body = "{}", string? presentedSecret = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (presentedSecret is not null) headers[SecretHeader] = presentedSecret;
        return new MessagingHttpRequest
        {
            Method = "POST",
            Body = Encoding.UTF8.GetBytes(body),
            Headers = headers,
        };
    }

    [Fact]
    public void TheProviderRegistersUnderTelegram()
    {
        Assert.Equal(MessagingChannel.ProviderTelegram, Telegram().Provider);
    }

    // ─── Verification ───────────────────────────────────────────────────

    [Fact]
    public async Task Verify_TheMatchingSecretTokenIsAccepted()
    {
        var result = await Telegram().VerifyAsync(Channel(), Post(presentedSecret: Secret), Secret, default);

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    [Fact]
    public async Task Verify_AWrongSecretTokenIsRejected()
    {
        var result = await Telegram().VerifyAsync(
            Channel(), Post(presentedSecret: "wrong"), Secret, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Equal(401, result.RejectStatusCode);
        Assert.Contains("secret token mismatch", result.RejectReason);
    }

    [Fact]
    public async Task Verify_AMissingSecretHeaderIsRejected()
    {
        var result = await Telegram().VerifyAsync(Channel(), Post(), Secret, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
    }

    // Default posture is deny: no configured secret and no explicit opt-in
    // leaves the endpoint open to anyone who guesses the URL.
    [Fact]
    public async Task Verify_NoConfiguredSecretAndNoOptInIsRejected()
    {
        var result = await Telegram().VerifyAsync(Channel(), Post(), null, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Contains("allow_unsigned", result.RejectReason);
    }

    [Fact]
    public async Task Verify_AllowUnsignedIsTheAdminsExplicitOptOut()
    {
        var result = await Telegram().VerifyAsync(
            Channel(allowUnsigned: true), Post(), null, default);

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    // Telegram only POSTs; a GET is a probe or a misconfiguration, so it is
    // acked rather than erroring — ParseInbound then yields nothing.
    [Fact]
    public async Task Verify_ANonPostIsAckedWithoutASecret()
    {
        var request = new MessagingHttpRequest { Method = "GET", Body = Array.Empty<byte>() };

        var result = await Telegram().VerifyAsync(Channel(), request, Secret, default);

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    // ─── Inbound parsing ────────────────────────────────────────────────

    private static string Update(
        long updateId = 1, long chatId = 500, long fromId = 42,
        string? text = "hola", bool isBot = false,
        string? username = "alice", string? firstName = "Alice")
    {
        var from = new Dictionary<string, object?> { ["id"] = fromId, ["is_bot"] = isBot };
        if (username is not null) from["username"] = username;
        if (firstName is not null) from["first_name"] = firstName;

        var message = new Dictionary<string, object?>
        {
            ["chat"] = new { id = chatId },
            ["from"] = from,
        };
        if (text is not null) message["text"] = text;

        return JsonSerializer.Serialize(new { update_id = updateId, message });
    }

    private static InboundMessage? Parse(string body)
        => Telegram().ParseInbound(Channel(), new MessagingHttpRequest
        {
            Method = "POST",
            Body = Encoding.UTF8.GetBytes(body),
        });

    [Fact]
    public void Parse_ReadsAPlainTextMessage()
    {
        var msg = Parse(Update());

        Assert.NotNull(msg);
        Assert.Equal("1", msg!.ProviderEventId);
        Assert.Equal("42", msg.ExternalUserId);
        // Telegram conversations are keyed on the chat.
        Assert.Equal("500", msg.ExternalThreadId);
        Assert.Equal("hola", msg.Text);
    }

    // Bots can see each other's messages in a group; echoing one back would
    // loop forever.
    [Fact]
    public void Parse_BotAuthoredMessagesAreDropped()
    {
        Assert.Null(Parse(Update(isBot: true)));
    }

    // v1 handles plain new text messages only — edits, callbacks, channel
    // posts and media-only messages are out of scope and must not be
    // half-processed.
    [Theory]
    [InlineData("""{"update_id":1,"edited_message":{"chat":{"id":1},"from":{"id":2},"text":"x"}}""")]
    [InlineData("""{"update_id":1,"callback_query":{"id":"cb"}}""")]
    [InlineData("""{"update_id":1,"channel_post":{"chat":{"id":1},"text":"x"}}""")]
    public void Parse_NonMessageUpdatesAreIgnored(string body)
    {
        Assert.Null(Parse(body));
    }

    [Fact]
    public void Parse_AMediaOnlyMessageIsIgnored()
    {
        Assert.Null(Parse(Update(text: null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Parse_MalformedPayloadsYieldNothing(string body)
    {
        Assert.Null(Parse(body));
    }

    // The display name prefers @username and falls back to the first name so
    // the link-confirmation UI always has something to show.
    [Fact]
    public void Parse_TheDisplayNamePrefersTheUsername()
    {
        Assert.Equal("alice", Parse(Update(username: "alice", firstName: "Alice"))!.SenderDisplayName);
    }

    [Fact]
    public void Parse_TheDisplayNameFallsBackToTheFirstName()
    {
        Assert.Equal("Alice", Parse(Update(username: null, firstName: "Alice"))!.SenderDisplayName);
    }

    [Fact]
    public void Parse_AnAnonymousSenderHasNoDisplayName()
    {
        Assert.Null(Parse(Update(username: null, firstName: null))!.SenderDisplayName);
    }

    // ─── Sending ────────────────────────────────────────────────────────

    private static OutboundMessage Outbound(string threadId = "500", string text = "listo")
        => new() { ExternalThreadId = threadId, Text = text };

    [Fact]
    public async Task Send_PostsToSendMessageWithTheBotTokenInThePath()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""");

        await Telegram(http).SendAsync(Channel(), "12345:AAbb", Outbound(), default);

        var url = http.Requests[^1].RequestUri!.AbsoluteUri;
        Assert.StartsWith("https://api.telegram.org/bot12345:AAbb/sendMessage", url);
    }

    // A numeric chat id must be sent as a JSON number; Telegram rejects the
    // quoted form for user/group chats.
    [Fact]
    public async Task Send_ANumericChatIdIsSentAsANumber()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""");

        await Telegram(http).SendAsync(Channel(), "tok", Outbound("500"), default);

        using var doc = JsonDocument.Parse(http.RequestBodies[^1]);
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("chat_id").ValueKind);
    }

    // A channel @username is not numeric and rides as a string.
    [Fact]
    public async Task Send_AChannelUsernameIsSentAsAString()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""");

        await Telegram(http).SendAsync(Channel(), "tok", Outbound("@ops_channel"), default);

        using var doc = JsonDocument.Parse(http.RequestBodies[^1]);
        Assert.Equal("@ops_channel", doc.RootElement.GetProperty("chat_id").GetString());
    }

    [Fact]
    public async Task Send_WithoutABotTokenIsRefusedBeforeAnyCall()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Telegram(http).SendAsync(Channel(), null, Outbound(), default));

        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Send_AnHttpErrorIsAFailure()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.BadRequest, """{"ok":false}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Telegram(http).SendAsync(Channel(), "tok", Outbound(), default));
    }

    // Telegram caps a message at 4096 characters; a longer agent answer is
    // truncated rather than rejected by the API.
    [Fact]
    public async Task Send_TheTextIsTruncatedToTelegramsLimit()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""");

        await Telegram(http).SendAsync(
            Channel(), "tok", Outbound(text: new string('x', 9000)), default);

        using var doc = JsonDocument.Parse(http.RequestBodies[^1]);
        Assert.True(doc.RootElement.GetProperty("text").GetString()!.Length <= 4096);
    }
}


