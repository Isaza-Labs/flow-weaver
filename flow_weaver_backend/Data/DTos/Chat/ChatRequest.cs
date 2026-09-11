using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class ChatRequest
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("agent_id")]
    public Guid? AgentId { get; set; }

    [JsonPropertyName("conversation_id")]
    public Guid? ConversationId { get; set; }

    [JsonPropertyName("provider_id")]
    public Guid? ProviderId { get; set; }

    [JsonPropertyName("model_override")]
    public string? ModelOverride { get; set; }

    [JsonPropertyName("context")]
    public JsonElement? Context { get; set; }
}
