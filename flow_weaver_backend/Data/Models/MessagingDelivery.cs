namespace flow_weaver_backend.Models;

// Append-only audit row for every outbound message attempt (the agent's reply,
// or the account-linking prompt). Retried sends bump Attempt; a terminal
// failure lands as Status=failed with the last Error. Retention sweeper caps
// the table (F7), like trace_events.
public class MessagingDelivery : BaseModel
{
    // pending | sent | failed
    public const string StatusPending = "pending";
    public const string StatusSent = "sent";
    public const string StatusFailed = "failed";

    public Guid MessagingDeliveryId { get; set; }
    public Guid MessagingChannelId { get; set; }
    public Guid? ConversationId { get; set; }
    public string? ExternalThreadId { get; set; }
    public string Status { get; set; } = StatusPending;
    public int Attempt { get; set; }
    public string? Error { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}
