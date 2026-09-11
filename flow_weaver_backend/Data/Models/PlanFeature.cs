using System.Text.Json;

namespace flow_weaver_backend.Models;

// S16 — Harness lesson 8: feature lists as primitives. Materializes one
// row per discrete construction step the agent (or import wizard) is
// working through, so multi-session work survives worker restarts and
// the user can see live progress.
//
// Ordinal preserves authoring order. Status is the single field worth
// querying — list_plan_features rebuilds a checklist by filtering rows
// of one plan.
public class PlanFeature : BaseModel
{
    public Guid PlanFeatureId { get; set; }

    // A feature belongs to either a WorkflowPlan (governance flow) or a
    // workflow import draft token (analyzed but not yet committed).
    // Exactly one of the two is non-null. ImportToken is a string instead
    // of a Guid because the wizard's token is a base64 string, not a UUID.
    public Guid? WorkflowPlanId { get; set; }
    public string? ImportToken { get; set; }

    public int Ordinal { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = PlanFeatureStatus.Pending;
    public string? SnippetType { get; set; }

    // Free-form criteria the agent attached when creating the feature.
    // Example: {"simulate_ok": true, "produces_output": "device_list"}.
    public JsonElement Acceptance { get; set; } = default;

    // The tool name (or "import_pipeline") that verified the feature.
    // Stamped by list_plan_features helpers and pipeline transitions so
    // the UI can show "verified by simulate_workflow_run".
    public string? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? RejectionReason { get; set; }
}

public static class PlanFeatureStatus
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Verified = "verified";
    public const string Rejected = "rejected";
    public const string Skipped = "skipped";

    public static readonly string[] All =
    {
        Pending, InProgress, Verified, Rejected, Skipped,
    };
}
