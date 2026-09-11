using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateWorkflow
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("nodes")]
    public JsonElement? Nodes { get; set; }

    [JsonPropertyName("edges")]
    public JsonElement? Edges { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; set; }

    [JsonPropertyName("conversation_id")]
    public Guid? ConversationId { get; set; }
}
