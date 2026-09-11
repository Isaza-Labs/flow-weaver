using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace flow_weaver_backend.Services.Worker.Handlers;

// Native Slack snippet: posts a message to a channel via chat.postMessage.
//
// Unlike the integration_action route (which keeps the token in an encrypted
// Integration), this handler reads the bot token from DEPLOYMENT config —
// `Slack:BotToken` (env `SLACK_BOT_TOKEN`) — so it behaves like a built-in
// snippet (ping / rest_call / report). The trade-off: the token is a plain
// environment secret, not encrypted at rest; that's the deliberate choice for a
// single shared workspace.
//
// Input (the node's config_overrides, resolved before dispatch):
//   { "channel": "#alerts" | "C0123456789", "text": "…", "thread_ts": "…"? }
//
// The bot must be a member of the target channel and the token needs the
// `chat:write` scope. Slack answers HTTP 200 even on logical failure, so the
// real outcome is the `ok` field of the response.
public sealed class SlackHandler : ISnippetHandler
{
    public const string SnippetType = "slack_message";
    public const string HttpClientName = "slack";
    private const string PostMessageUrl = "https://slack.com/api/chat.postMessage";

    public string Type => SnippetType;

    // A sent chat message has no automatic undo (same class as "send an email"
    // in the IdempotencyKind docs). Promotion warns; rollback refuses.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.NonReversible;

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<SlackHandler> _logger;

    public SlackHandler(
        IHttpClientFactory httpFactory, IConfiguration config, ILogger<SlackHandler> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var token = _config["Slack:BotToken"];
        if (string.IsNullOrWhiteSpace(token))
            return Fail("Slack bot token is not configured — set Slack:BotToken (env SLACK_BOT_TOKEN).");

        // Tolerate both a flat payload and a nested `input` object (matches how
        // python_snippet accepts either shape).
        // `via` names another product's messaging-channel record; this
        // handler posts with the deployment token, so the key is dropped
        // rather than mistaken for a typo (snippets/SPEC.md `slack_message`).
        var input = PayloadAliases.Normalize(request.SnippetType, request.InputPayload);
        var src = input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("input", out var inner) && inner.ValueKind == JsonValueKind.Object
                ? inner
                : input;

        var channel = GetString(src, "channel");
        var text = GetString(src, "text");
        if (string.IsNullOrWhiteSpace(channel))
            return Fail("channel is required (e.g. \"#alerts\" or a channel ID like C0123456789)");
        if (string.IsNullOrWhiteSpace(text))
            return Fail("text is required");

        var bodyJson = BuildBody(channel, text, GetString(src, "thread_ts"));

        var client = _httpFactory.CreateClient(HttpClientName);
        using var msg = new HttpRequestMessage(HttpMethod.Post, PostMessageUrl);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        msg.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(msg, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "worker.slack.failed step_run_id={StepRunId} channel={Channel}", request.StepRunId, channel);
            return Fail($"slack request failed: {ex.Message}");
        }
        using var _ = resp;

        var raw = await resp.Content.ReadAsStringAsync(ct);

        // Slack returns HTTP 200 even when it rejects the message; the truth is
        // in `ok` + `error`.
        var ok = false;
        string? slackError = null;
        string? ts = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("error", out var errEl)) slackError = errEl.GetString();
            if (root.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.String)
                ts = tsEl.GetString();
        }
        catch (JsonException)
        {
            slackError = "non-JSON response from Slack";
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            ok,
            channel,
            ts,
            error = slackError,
            status_code = (int)resp.StatusCode,
        });

        if (ok)
            _logger.LogInformation(
                "worker.slack.ok step_run_id={StepRunId} channel={Channel} ts={Ts}",
                request.StepRunId, channel, ts);
        else
            _logger.LogWarning(
                "worker.slack.failed step_run_id={StepRunId} channel={Channel} error={Error}",
                request.StepRunId, channel, slackError ?? Tail(raw, 200));

        return new SnippetResult
        {
            Success = ok,
            Output = output,
            // A posted message is visible to everyone in the channel and this product
            // never deletes one, so a successful post changed something. Slack answering
            // `ok: false` means nothing was posted.
            Change = ok ? StepChange.Changed : StepChange.Unchanged,
            Logs = $"POST chat.postMessage channel={channel} → {(int)resp.StatusCode} ok={ok}",
            Error = ok ? string.Empty : $"slack error: {slackError ?? Tail(raw, 200)}",
        };
    }

    private static string BuildBody(string channel, string text, string? threadTs)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("channel", channel);
            w.WriteString("text", text);
            if (!string.IsNullOrWhiteSpace(threadTs)) w.WriteString("thread_ts", threadTs);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

    private static string Tail(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[^max..]);

    private static SnippetResult Fail(string error) => new() { // A step that failed before its action did anything changed nothing.
        Change = StepChange.Unchanged, Success = false, Error = error };
}
