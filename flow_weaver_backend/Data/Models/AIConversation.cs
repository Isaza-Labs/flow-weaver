using System.Text.Json;

namespace flow_weaver_backend.Models;

public class AIConversation : BaseModel
{
    public Guid AIConversationId { get; set; }
    public string? UserId { get; set; }
    public Guid? AgentId { get; set; }
    // RESERVED since the initial schema — no code path has ever written it.
    // Not exposed by the API. Wire it up (or drop the column) before use.
    public Guid? ParentConversationId { get; set; }
    public JsonElement Messages { get; set; } = default;
    // RESERVED since the initial schema — no code path has ever written it,
    // so AiConversationsController deliberately does not return it (it was a
    // permanent `context: null` in the frontend). Give it a writer before
    // surfacing it again.
    public JsonElement Context { get; set; } = default;
    public string Status { get; set; } = string.Empty;
    public JsonElement TokenUsage { get; set; } = default;

    // Origin of the conversation. Null/"web" for the chat UI; "telegram",
    // "slack", "whatsapp", "teams" for external channels. With
    // MessagingChannelId + ExternalThreadId the worker resolves an inbound
    // external thread back to its conversation on every turn.
    public string? Source { get; set; }
    public Guid? MessagingChannelId { get; set; }
    public string? ExternalThreadId { get; set; }
}
