using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class WorkflowRunResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("workflow_id")]
    public Guid WorkflowId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("input_payload")]
    public JsonElement InputPayload { get; set; }

    [JsonPropertyName("target_devices")]
    public List<Guid> TargetDevices { get; set; } = new();

    [JsonPropertyName("target_pools")]
    public List<Guid> TargetPools { get; set; } = new();

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = string.Empty;

    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }

    // Set only when the run failed OUTSIDE any step (orchestration). An
    // ordinary step failure leaves this null — the failing step's own error is
    // the better answer there.
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    // ─── run outcome (workflow-v1/run-outcome) ──────────────────────────────
    //
    // `status` says whether the run finished; these say what it left behind. Null on runs
    // recorded before the model existed, which is what null means everywhere it appears
    // here — "not recorded", never "nothing changed".

    /// <summary>completed | rolled_back | failed.</summary>
    /// <remarks>
    /// `rolled_back` means every change this run made COULD have been undone. It does NOT
    /// mean anything was undone — nothing executes the plan below, in either product.
    /// </remarks>
    [JsonPropertyName("final_state")]
    public string? FinalState { get; set; }

    /// <summary>How many steps reported changing something.</summary>
    [JsonPropertyName("changed_count")]
    public int? ChangedCount { get; set; }

    /// <summary>Reversible changed nodes, in reverse execution order.</summary>
    [JsonPropertyName("rollback_plan")]
    public IReadOnlyList<string>? RollbackPlan { get; set; }
}
