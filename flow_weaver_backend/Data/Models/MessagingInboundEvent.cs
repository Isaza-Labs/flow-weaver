namespace flow_weaver_backend.Models;

// Append-only audit row for every inbound webhook delivery (verified or not),
// and the idempotency key for the channel. Providers re-deliver when a webhook
// is slow to ack, so (MessagingChannelId, ProviderEventId) is unique and a
// repeat is dropped. The raw body is deliberately NOT persisted (PII + size) —
// only the metadata that explains what happened, mirroring GitWebhookDelivery.
public class MessagingInboundEvent : BaseModel
{
    // received | verified | rejected | queued | processing | completed | failed.
    // queued → processing is the atomic idempotency claim the worker makes so a
    // reclaimed job can't run the agent twice (at-most-once turn processing).
    public const string StatusReceived = "received";
    public const string StatusVerified = "verified";
    public const string StatusRejected = "rejected";
    public const string StatusQueued = "queued";
    public const string StatusProcessing = "processing";
    public const string StatusCompleted = "completed";
    public const string StatusFailed = "failed";

    public Guid MessagingInboundEventId { get; set; }
    public Guid MessagingChannelId { get; set; }

    // Provider-native event id: Slack event_id, Telegram update_id, WhatsApp
    // message id, Teams activity id. Unique per channel for dedupe.
    public string ProviderEventId { get; set; } = string.Empty;

    // thread_ts / chat_id / from — the external thread this message belongs to.
    public string? ExternalThreadId { get; set; }

    // The AIConversation this event resolved to (null if rejected/unlinked).
    public Guid? ConversationId { get; set; }

    public string Status { get; set; } = StatusReceived;

    // message | mention | other — best-effort classification.
    public string? Event { get; set; }

    public string? Error { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}
