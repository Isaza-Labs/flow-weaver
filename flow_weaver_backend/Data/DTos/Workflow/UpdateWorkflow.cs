using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// PUT body for /api/workflow/{id}. Does NOT expose Version / Environment /
// PromotedFrom / PromotedAt / CreatedBy — those are managed by the system
// (version bumps auto, environment is managed by the promotion flow).
public class UpdateWorkflow
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

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

    [JsonPropertyName("change_summary")]
    public string? ChangeSummary { get; set; }

    [JsonPropertyName("conversation_id")]
    public Guid? ConversationId { get; set; }
}
