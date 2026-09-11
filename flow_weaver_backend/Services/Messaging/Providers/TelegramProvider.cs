using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Services.Messaging.Providers;

// Telegram Bot API channel. Inbound is verified by the secret_token the bot
// owner set with setWebhook (echoed back in the X-Telegram-Bot-Api-Secret-Token
// header); there is no challenge handshake. Outbound posts to the Bot API
// sendMessage with the configured bot token.
public sealed class TelegramProvider : IMessagingProvider
{
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    private const int MaxMessageChars = 4096;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TelegramProvider> _logger;

    public TelegramProvider(IHttpClientFactory httpFactory, ILogger<TelegramProvider> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public string Provider => MessagingChannel.ProviderTelegram;

    public Task<WebhookVerifyResult> VerifyAsync(
        MessagingChannel channel, MessagingHttpRequest request,
        string? decryptedSigningSecret, CancellationToken ct)
    {
        // Telegram only POSTs updates; a GET is a misconfiguration — ack so a
        // probe doesn't error, ParseInbound will return null.
        if (!HttpMethods.IsPost(request.Method))
            return Task.FromResult(WebhookVerifyResult.Verified());

        if (string.IsNullOrEmpty(decryptedSigningSecret))
        {
            return Task.FromResult(channel.AllowUnsigned
                ? WebhookVerifyResult.Verified()
                : WebhookVerifyResult.Rejected(401, "no secret token configured; set one or enable allow_unsigned"));
        }

        var presented = request.Header(SecretHeader);
        var ok = presented is not null && FixedTimeEquals(presented, decryptedSigningSecret);
        return Task.FromResult(ok
            ? WebhookVerifyResult.Verified()
            : WebhookVerifyResult.Rejected(401, "secret token mismatch"));
    }

    public InboundMessage? ParseInbound(MessagingChannel channel, MessagingHttpRequest request)
    {
        if (request.Body.Length == 0) return null;
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(request.Body);
        }
        catch (JsonException)
        {
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object) return null;

        // Only handle plain new messages with text for v1 (skip edited_message,
        // callback_query, channel_post, media-only messages).
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return null;
        if (!message.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
            return null;

        // Skip messages authored by any bot (including this one) to prevent
        // reply loops in groups where bots can see each other's messages.
        if (message.TryGetProperty("from", out var fromObj)
            && fromObj.TryGetProperty("is_bot", out var isBot)
            && isBot.ValueKind == JsonValueKind.True)
            return null;

        var updateId = root.TryGetProperty("update_id", out var u) ? u.ToString() : null;
        var chatId = message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var cid)
            ? cid.ToString() : null;
        var fromId = message.TryGetProperty("from", out var from) && from.TryGetProperty("id", out var fid)
            ? fid.ToString() : null;
        if (updateId is null || chatId is null || fromId is null) return null;

        string? display = null;
        if (message.TryGetProperty("from", out var f))
        {
            display = (f.TryGetProperty("username", out var un) && un.ValueKind == JsonValueKind.String
                          ? un.GetString() : null)
                      ?? (f.TryGetProperty("first_name", out var fn) && fn.ValueKind == JsonValueKind.String
                          ? fn.GetString() : null);
        }

        return new InboundMessage
        {
            ProviderEventId = updateId,
            ExternalWorkspaceId = string.Empty, // Telegram has no workspace concept
            ExternalUserId = fromId,
            ExternalThreadId = chatId,
            Text = textEl.GetString() ?? string.Empty,
            SenderDisplayName = display,
            EventKind = "message",
        };
    }

    public async Task SendAsync(
        MessagingChannel channel, string? decryptedBotToken,
        OutboundMessage message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(decryptedBotToken))
            throw new InvalidOperationException("telegram channel has no bot token configured");

        var text = message.Text.Length > MaxMessageChars
            ? message.Text[..MaxMessageChars]
            : message.Text;

        // chat_id is numeric for user/group chats — send as a number when it
        // parses, otherwise as a string (channel @username).
        object chatId = long.TryParse(message.ExternalThreadId, out var numeric)
            ? numeric
            : message.ExternalThreadId;

        var payload = JsonSerializer.Serialize(new { chat_id = chatId, text });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var http = _httpFactory.CreateClient("messaging");
        var url = $"https://api.telegram.org/bot{decryptedBotToken}/sendMessage";
        using var resp = await http.PostAsync(url, content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "messaging.telegram.send_failed channel={Channel} status={Status} body={Body}",
                channel.MessagingChannelId, (int)resp.StatusCode, Truncate(body, 300));
            throw new InvalidOperationException($"telegram sendMessage failed: {(int)resp.StatusCode}");
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
