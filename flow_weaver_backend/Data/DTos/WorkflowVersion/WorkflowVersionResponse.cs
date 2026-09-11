using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class WorkflowVersionResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("workflow_id")]
    public Guid WorkflowId { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("nodes")]
    public JsonElement Nodes { get; set; }

    [JsonPropertyName("edges")]
    public JsonElement Edges { get; set; }

    [JsonPropertyName("services")]
    public JsonElement Services { get; set; }

    [JsonPropertyName("change_summary")]
    public string ChangeSummary { get; set; } = string.Empty;

    [JsonPropertyName("promoted_at")]
    public DateTime PromotedAt { get; set; }

    [JsonPropertyName("conversation_id")]
    public Guid? ConversationId { get; set; }

    [JsonPropertyName("promoted_by")]
    public string PromotedBy { get; set; } = string.Empty;

    [JsonPropertyName("test_run_id")]
    public Guid? TestRunId { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}
