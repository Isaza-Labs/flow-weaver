using System.Text.Json;

namespace flow_weaver_backend.Models;

public class StepRun : BaseModel
{
    public Guid StepRunId { get; set; }
    public Guid WorkflowRunId { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public Guid? SnippetId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Status { get; set; } = string.Empty;
    public JsonElement InputPayload { get; set; } = default;
    public JsonElement OutputPayload { get; set; } = default;
    public string Logs { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? WorkerId { get; set; }

    // Set on the parent's step_run when the node is a subflow invocation.
    // Lets the child's orchestrator find this row at completion time and
    // copy its terminal status + output payload up so the parent's
    // polling loop can advance past the subflow node.
    public Guid? ChildRunId { get; set; }

    // Whether this step CHANGED anything, as opposed to whether it succeeded.
    //
    // `Status` already says completed/failed/skipped; this says whether the action had an
    // effect, which is a different question and the one the run outcome is built from. A
    // `GET` that returned 200 and a `DELETE` that returned 200 are both `Completed`.
    //
    // Nullable and never backfilled: rows written before this column existed were recorded
    // under a model that had no such signal, and inventing one for them would fabricate
    // history. Null reads as "not recorded", which is what it is.
    //
    // An engine that runs the nodes itself can hold the signal in memory for the length
    // of a run. This product's steps run in a separate worker process, so the
    // flag has to survive the trip back — hence a column here and none there.
    public bool? ChangedState { get; set; }

    // The idempotency tier this step actually ran under, as a wire value
    // (`idempotent` | `requires_compensation` | `non_reversible`).
    //
    // Normally a tier is DERIVED — the handler's floor, the snippet's own tier under it, the
    // node's stricter-only override on top — so recording it would be storing a computation.
    // A `subflow` step is the exception that makes the column necessary: its tier belongs to
    // the CHILD run, is the strictest of whatever that run executed, and cannot be recomputed
    // from the parent's graph at all. Null means "derive it", which is every other step.
    //
    // The parent's rollback plan reads this. A child that sent an email has to keep the parent
    // out of it, and before this column there was nowhere for that fact to live.
    public string? Tier { get; set; }

    // A machine-readable reason this step failed, beside the human-readable `Error`.
    //
    // `Error` is written for a person and changes freely; a code is what a caller can branch
    // on and what a conformance vector compares. `subflow_failed` and `subflow_missing` are
    // the same sentence to a reader and completely different next steps to an operator: one
    // says go read the child run, the other says the child run does not exist.
    //
    // Null on steps that succeeded, and on failures recorded before codes existed.
    public string? ErrorCode { get; set; }
}
