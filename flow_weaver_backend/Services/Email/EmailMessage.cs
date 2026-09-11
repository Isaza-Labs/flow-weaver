namespace flow_weaver_backend.Services.Email;

// Transport-agnostic description of one outbound message. Built by the
// email_send handler from a step's config_overrides and by the channel
// test-send endpoint; consumed by IEmailSender.
public sealed record EmailMessage
{
    public IReadOnlyList<string> To { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Cc { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Bcc { get; init; } = Array.Empty<string>();

    public string Subject { get; init; } = string.Empty;

    // At least one body must be non-empty. When both are present the message
    // goes out as multipart/alternative and the client picks.
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }

    // Per-message overrides of the channel identity. Most providers reject a
    // From they don't own, so these are usually left null.
    public string? FromAddress { get; init; }
    public string? FromName { get; init; }
    public string? ReplyTo { get; init; }

    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = Array.Empty<EmailAttachment>();
}

// Attachment content is base64 in the step payload so a workflow can carry a
// report produced by an upstream `report` step without touching the filesystem.
public sealed record EmailAttachment(
    string FileName,
    string ContentBase64,
    string? ContentType);

// Outcome of one send attempt. `Detail` carries the SMTP server's response
// line for the operator; it never contains credentials.
public sealed record EmailSendResult(
    bool Ok,
    string? MessageId,
    string? Detail,
    string? Error,
    long ElapsedMs)
{
    public static EmailSendResult Failure(string error, long elapsedMs = 0) =>
        new(false, null, null, error, elapsedMs);
}
