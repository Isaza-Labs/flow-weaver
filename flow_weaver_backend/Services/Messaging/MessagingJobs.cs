using System.Text.Json;

namespace flow_weaver_backend.Services.Messaging;

// Job types + payloads for the messaging queue. Both ride the shared `jobs`
// table under the "messaging" tag, consumed by MessagingWorkerHostedService.
public static class MessagingJobTypes
{
    public const string Tag = "messaging";
    // Run the agent for an inbound user message, then enqueue a Send.
    public const string AgentMessage = "agent_message";
    // Push a single outbound message to the channel.
    public const string Send = "messaging_send";
}

public sealed class AgentMessagePayload
{
    public Guid ChannelId { get; init; }
    public Guid ConversationId { get; init; }
    public string ExternalThreadId { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    // The internal user whose CURRENT role the worker reads at process time —
    // the role is resolved fresh, never baked into the payload.
    public Guid LinkedUserId { get; init; }
    public Guid? AgentId { get; init; }
    // The inbound audit row that triggered this turn. The worker uses it as the
    // idempotency anchor: it atomically claims it (queued → processing) so a
    // reclaimed job won't re-run the agent.
    public Guid MessagingInboundEventId { get; init; }
}

public sealed class SendPayload
{
    public Guid ChannelId { get; init; }
    public string ExternalThreadId { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public Guid? ConversationId { get; init; }
    // 1-based attempt counter. On a failed send the worker re-enqueues with
    // Attempt+1 until MessagingOptions.MaxSendAttempts is reached.
    public int Attempt { get; init; } = 1;
}

internal static class MessagingJobJson
{
    private static readonly JsonSerializerOptions Options =
        new() { PropertyNameCaseInsensitive = true };

    public static JsonElement Serialize<T>(T payload) => JsonSerializer.SerializeToElement(payload);

    public static T? Deserialize<T>(JsonElement payload) => payload.Deserialize<T>(Options);
}
