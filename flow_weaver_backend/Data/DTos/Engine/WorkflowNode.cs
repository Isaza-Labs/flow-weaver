using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// One node in a workflow DAG. Wire format of an entry inside Workflow.Nodes (jsonb).
// The UI places nodes by X/Y; the engine only cares about Id, SnippetId, and ConfigOverrides.
public class WorkflowNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("snippet_id")]
    public string SnippetId { get; set; } = string.Empty;

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("config_overrides")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ConfigOverrides { get; set; }
}
