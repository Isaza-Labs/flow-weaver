using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Email;

namespace flow_weaver_backend.Services.Worker.Handlers;

// Native SMTP snippet: sends a message through a configured EmailChannel.
//
// Unlike SlackHandler (whose token is a deployment env var), the credential
// lives in an encrypted EmailChannel row — the same model as MessagingChannel
// and McpServer — so a deployment can hold several relays and a step picks one.
//
// Input (the node's config_overrides, resolved before dispatch):
//   {
//     "channel": "ops-relay",         // channel NAME (canonical); `channel_id`
//                                     // is the accepted alias. Omit both to
//                                     // use the default channel.
//     "to": "a@x.com" | ["a@x.com"],  // to/cc/bcc accept a string or an array
//     "cc": [...], "bcc": [...],
//     "subject": "…",
//     "body": "plain text",           // body and/or html; at least one required
//     "html": "<p>…</p>",
//     "from_address": "…", "from_name": "…", "reply_to": "…",   // optional overrides
//     "attachments": [{ "file_name": "r.pdf", "content_base64": "…",
//                       "content_type": "application/pdf" }]
//   }
public sealed class EmailSendHandler : ISnippetHandler
{
    public const string SnippetType = "email_send";

    public string Type => SnippetType;

    // A delivered email cannot be recalled. Promotion warns on these and
    // PromotionService.RollbackAsync refuses to roll back a graph holding one.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.NonReversible;

    private readonly IEmailChannelRepository _channels;
    private readonly IEmailSender _sender;
    private readonly ILogger<EmailSendHandler> _logger;

    public EmailSendHandler(
        IEmailChannelRepository channels,
        IEmailSender sender,
        ILogger<EmailSendHandler> logger)
    {
        _channels = channels;
        _sender = sender;
        _logger = logger;
    }

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        // Tolerate both a flat payload and a nested `input` object (matches how
        // python_snippet and slack_message accept either shape).
        var payload = request.InputPayload;
        var src = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("input", out var inner) && inner.ValueKind == JsonValueKind.Object
                ? inner
                : payload;

        // `channel` NAMES the relay and is the canonical key; `channel_id` is
        // this product's local-id spelling, accepted as an alias
        // (snippets/SPEC.md `email_send`). Reading only the id meant an
        // imported `{ "channel": "ops-relay" }` matched nothing here, fell
        // through to the instance default, and sent from the wrong relay
        // without a word — and because SnippetKeyCatalog lists BOTH keys as
        // known, the import did not note it either. A named channel that does
        // not exist is an error, never a fallback: the default relay is the
        // one thing the author demonstrably did not ask for.
        Models.EmailChannel? channel;
        var wantedChannel = GetString(src, "channel");
        var rawChannelId = GetString(src, "channel_id");

        if (!string.IsNullOrWhiteSpace(wantedChannel))
        {
            var wanted = wantedChannel!.Trim();
            channel = Guid.TryParse(wanted, out var namedId)
                ? await _channels.FindEnabledByIdAsync(namedId, ct)
                : await _channels.FindEnabledByNameAsync(wanted, ct);
            if (channel is null)
                return Fail(
                    $"not_found: no enabled email channel named '{wanted}'. Register the relay in "
                    + "/email under that name — this step will not fall back to the default one.");
        }
        else if (!string.IsNullOrWhiteSpace(rawChannelId))
        {
            if (!Guid.TryParse(rawChannelId, out var channelId))
                return Fail($"channel_id must be a GUID (got '{rawChannelId}')");
            channel = await _channels.FindEnabledByIdAsync(channelId, ct);
            if (channel is null)
                return Fail($"email channel {channelId} not found, disabled or deleted");
        }
        else
        {
            channel = await _channels.FindDefaultAsync(ct);
            if (channel is null)
                return Fail(
                    "no channel was given and no default email channel is configured. "
                    + "Set one in /email, or name a channel with `channel`.");
        }

        EmailMessage message;
        try
        {
            message = BuildMessage(src);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        var result = await _sender.SendAsync(channel, message, ct);

        // Bookkeeping the /email UI reads ("last send"). Best-effort: a
        // bookkeeping failure must never flip the outcome of a send that
        // already happened.
        try
        {
            await _channels.MarkSendOutcomeAsync(channel.EmailChannelId, result.Ok, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "worker.email_send.mark_outcome_failed channel_id={ChannelId}", channel.EmailChannelId);
        }

        var recipients = message.To.Count + message.Cc.Count + message.Bcc.Count;
        var output = JsonSerializer.SerializeToElement(new
        {
            ok = result.Ok,
            channel_id = channel.EmailChannelId,
            channel_name = channel.Name,
            provider = channel.Provider,
            message_id = result.MessageId,
            recipients,
            elapsed_ms = result.ElapsedMs,
            error = result.Error,
        });

        if (result.Ok)
            _logger.LogInformation(
                "worker.email_send.ok step_run_id={StepRunId} channel_id={ChannelId} recipients={Recipients}",
                request.StepRunId, channel.EmailChannelId, recipients);
        else
            _logger.LogWarning(
                "worker.email_send.failed step_run_id={StepRunId} channel_id={ChannelId} error={Error}",
                request.StepRunId, channel.EmailChannelId, result.Error);

        return new SnippetResult
        {
            Success = result.Ok,
            Output = output,
            // A delivered message is a change nothing can take back — that is why this
            // type is NonReversible. A relay that refused delivered nothing.
            Change = result.Ok ? StepChange.Changed : StepChange.Unchanged,
            // Recipient count, not the addresses: step logs land in traces.
            Logs = $"SMTP {channel.Provider} {channel.Host}:{channel.Port} "
                + $"→ {recipients} recipient(s) ok={result.Ok}",
            Error = result.Ok ? string.Empty : result.Error ?? "email send failed",
        };
    }

    internal static EmailMessage BuildMessage(JsonElement src)
    {
        var to = ReadAddresses(src, "to");
        var cc = ReadAddresses(src, "cc");
        var bcc = ReadAddresses(src, "bcc");
        if (to.Count == 0 && cc.Count == 0 && bcc.Count == 0)
            throw new ArgumentException("to is required (a string or an array of addresses)");

        var subject = GetString(src, "subject");
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("subject is required");

        var body = GetString(src, "body");
        var html = GetString(src, "html");
        if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(html))
            throw new ArgumentException("body or html is required");

        return new EmailMessage
        {
            To = to,
            Cc = cc,
            Bcc = bcc,
            Subject = subject!,
            TextBody = body,
            HtmlBody = html,
            FromAddress = GetString(src, "from_address"),
            FromName = GetString(src, "from_name"),
            ReplyTo = GetString(src, "reply_to"),
            Attachments = ReadAttachments(src),
        };
    }

    // A recipient field accepts a bare string, a comma-separated string, or an
    // array — templates that fan out from an upstream step produce arrays, and
    // hand-authored nodes almost always produce one string.
    private static IReadOnlyList<string> ReadAddresses(JsonElement src, string name)
    {
        if (src.ValueKind != JsonValueKind.Object
            || !src.TryGetProperty(name, out var v)
            || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Array.Empty<string>();

        if (v.ValueKind == JsonValueKind.String)
            return Split(v.GetString());

        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .SelectMany(e => Split(e.GetString()))
                .ToList();

        throw new ArgumentException($"{name} must be a string or an array of strings");
    }

    private static List<string> Split(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new List<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

    private static IReadOnlyList<EmailAttachment> ReadAttachments(JsonElement src)
    {
        if (src.ValueKind != JsonValueKind.Object
            || !src.TryGetProperty("attachments", out var v)
            || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<EmailAttachment>();

        var list = new List<EmailAttachment>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("every entry of attachments must be an object");
            var fileName = GetString(item, "file_name");
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("every attachment needs a file_name");
            var content = GetString(item, "content_base64");
            if (string.IsNullOrWhiteSpace(content))
                throw new ArgumentException($"attachment '{fileName}' needs content_base64");
            list.Add(new EmailAttachment(fileName!, content!, GetString(item, "content_type")));
        }
        return list;
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

    private static SnippetResult Fail(string error) => new() { // A step that failed before its action did anything changed nothing.
        Change = StepChange.Unchanged, Success = false, Error = error };
}
