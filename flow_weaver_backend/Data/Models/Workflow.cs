using System.Text.Json;

namespace flow_weaver_backend.Models;

public class Workflow : BaseModel
{
    public Guid WorkflowId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Version { get; set; }

    // Matches the JSON Schema the Nodes/Edges payload was validated against
    // at write time. Stamped by WorkflowService; lets a future v2 coexist
    // without rewriting historical rows.
    public string SchemaVersion { get; set; } = "v1";
    public JsonElement InputSchema { get; set; } = default;
    public JsonElement Nodes { get; set; } = default;
    public JsonElement Edges { get; set; } = default;
    public JsonElement Metadata { get; set; } = default;
    public string? CreatedBy { get; set; }
    public string Environment { get; set; } = string.Empty;
    public Guid? PromotedFrom { get; set; }
    public string ChangeSummary { get; set; } = string.Empty;
    public DateTime? PromotedAt { get; set; }
    public Guid? ConversationId { get; set; }

    // Pointer to the most recent SimulationResult row whose SchemaHash
    // matches the current (Nodes, Edges). PromotionService's draft→qa
    // gate requires this to be set and Ok=true. Null on rows that have
    // never been simulated; gets reset to null when the DAG is edited.
    public Guid? LastSimulationId { get; set; }
}
