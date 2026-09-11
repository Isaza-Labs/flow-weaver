using System.Text.Json;

namespace flow_weaver_backend.Models;

// Materialized output of a `simulate_workflow_run` dispatch. Persisted
// so PromotionService can enforce "draft→qa requires a fresh successful
// simulation" without having to re-run the simulator.
//
// SchemaHash is a deterministic fingerprint of (Nodes, Edges) at simulation
// time. PromotionService and MarkWorkflowReadyHandler compare it against
// the current workflow to detect staleness — if the author edited the
// DAG after simulating, the row is invalid as evidence.
public class SimulationResult : BaseModel
{
    public Guid SimulationResultId { get; set; }
    public Guid WorkflowId { get; set; }
    public int NodeCount { get; set; }
    public int IssueCount { get; set; }
    public int WarningCount { get; set; }
    public bool Ok { get; set; }

    // SHA-256 over the canonicalized graph (`{nodes,edges}` raw JSON,
    // computed in SimulateWorkflowRunHandler). Hex-encoded so it fits
    // naturally in logs and admin UI.
    public string SchemaHash { get; set; } = string.Empty;

    public JsonElement Issues { get; set; } = default;
    public JsonElement Warnings { get; set; } = default;
    public DateTime SimulatedAt { get; set; }
    public string SimulatedBy { get; set; } = string.Empty;
}
