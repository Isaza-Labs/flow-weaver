using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Directed edge between two nodes. Wire format of an entry inside Workflow.Edges (jsonb).
// Valid Type values: "success", "failure", "always", "conditional". Defaults to "success"
// when missing. "conditional" edges carry an Expression in Condition (evaluated at runtime).
public class WorkflowEdge
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "success";

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    // Visual-only metadata. The engine's DAG parser ignores these — they
    // exist so the frontend can render each edge from the exact side the
    // operator connected (top/right/bottom/left) instead of defaulting to
    // the first handle registered on the node component.
    [JsonPropertyName("source_handle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceHandle { get; set; }

    [JsonPropertyName("target_handle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetHandle { get; set; }
}
