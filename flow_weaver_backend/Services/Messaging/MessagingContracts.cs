namespace flow_weaver_backend.Services.Messaging;

// A normalized view of an inbound HTTP webhook request, decoupled from
// ASP.NET so providers stay testable. Header/query lookups are
// case-insensitive. Body is the raw bytes (needed verbatim for HMAC).
public sealed class MessagingHttpRequest
{
    public required string Method { get; init; }
    public required byte[] Body { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> Query { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public string? QueryValue(string name) => Query.TryGetValue(name, out var v) ? v : null;
}

public enum WebhookVerifyOutcome
{
    Verified,
    // The provider's verification handshake wants a specific body echoed back
    // (Slack url_verification challenge, WhatsApp hub.challenge GET).
    Challenge,
    Rejected,
}

public sealed class WebhookVerifyResult
{
    public WebhookVerifyOutcome Outcome { get; init; }
    public string? ChallengeBody { get; init; }
    public string ChallengeContentType { get; init; } = "text/plain";
    public int RejectStatusCode { get; init; }
    public string? RejectReason { get; init; }

    public static WebhookVerifyResult Verified() =>
        new() { Outcome = WebhookVerifyOutcome.Verified };

    public static WebhookVerifyResult Challenge(string body, string contentType = "text/plain") =>
        new() { Outcome = WebhookVerifyOutcome.Challenge, ChallengeBody = body, ChallengeContentType = contentType };

    public static WebhookVerifyResult Rejected(int statusCode, string reason) =>
        new() { Outcome = WebhookVerifyOutcome.Rejected, RejectStatusCode = statusCode, RejectReason = reason };
}

// A user message extracted from an inbound payload. ParseInbound returns null
// when the payload is not a routable user message (the provider's own echo, a
// delivery receipt, a non-message event).
public sealed class InboundMessage
{
    public required string ProviderEventId { get; init; }
    // "" when the provider has no workspace concept (Telegram). The ingest
    // coalesces null → "" to match the identity unique index.
    public string ExternalWorkspaceId { get; init; } = string.Empty;
    public required string ExternalUserId { get; init; }
    public required string ExternalThreadId { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? SenderDisplayName { get; init; }
    // message | mention | other — best-effort, stored on the inbound audit row.
    public string EventKind { get; init; } = "message";
}

// A message to push back to the external channel.
public sealed class OutboundMessage
{
    public required string ExternalThreadId { get; init; }
    public required string Text { get; init; }
}
