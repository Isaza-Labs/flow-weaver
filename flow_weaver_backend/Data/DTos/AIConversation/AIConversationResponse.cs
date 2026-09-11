using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class AIConversationResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("agent_id")]
    public Guid? AgentId { get; set; }

    [JsonPropertyName("parent_conversation_id")]
    public Guid? ParentConversationId { get; set; }

    [JsonPropertyName("messages")]
    public JsonElement Messages { get; set; }

    [JsonPropertyName("context")]
    public JsonElement Context { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("token_usage")]
    public JsonElement TokenUsage { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
