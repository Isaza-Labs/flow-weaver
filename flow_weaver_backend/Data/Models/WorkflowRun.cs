using System.Text.Json;

namespace flow_weaver_backend.Models;

public class WorkflowRun : BaseModel
{
    public Guid WorkflowRunId { get; set; }
    public Guid WorkflowId { get; set; }
    public string Status { get; set; } = string.Empty;
    public JsonElement InputPayload { get; set; } = default;
    public List<Guid> TargetDevices { get; set; } = new();
    public List<Guid> TargetPools { get; set; } = new();
    public string Trigger { get; set; } = string.Empty;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CreatedBy { get; set; }
    public Guid? ParentRunId { get; set; }

    // Why the run failed when NO step can say. Orchestration can fail before or
    // between steps — an unresolvable snippet_id, a malformed DAG, a target set
    // that resolves to nothing — and until this column existed those failures
    // set Status=failed and recorded the reason only in the worker's log. The
    // UI could only say "the engine marked the run as failed but no step
    // reported an error message", which is a dead end for whoever has to fix it.
    //
    // Null on success and on ordinary step failures, where the failing
    // StepRun.Error is the better answer.
    public string? Error { get; set; }

    // The canonical hash of the GRAPH this run executed, stamped when the run starts.
    //
    // Same field and same name as the shared contract's SchemaHash, and the reason
    // for it is simple: a workflow is edited between runs, so "which version of the
    // graph did this run actually execute" is a question only the run row can answer. It is
    // also the audit.v1 member of that name.
    //
    // Nullable for runs recorded before it existed.
    public string? SchemaHash { get; set; }

    // ─── run outcome (workflow-v1/run-outcome) ──────────────────────────────
    //
    // `Status` says whether the run finished; these say what it LEFT BEHIND. Same names and
    // same wire values as other engines, because the shared contract defines them:
    //
    //   FinalState       completed | rolled_back | failed
    //                    A failed run whose changed steps were all reversible is
    //                    `rolled_back`; one that changed something irreversible is `failed`.
    //   ChangedCount     how many steps reported a change.
    //   RollbackPlanJson the reversible changed nodes in reverse execution order.
    //
    // NOTHING EXECUTES THE PLAN, in either product. It is a classification of what ran, not
    // a promise that anything was undone — `rolled_back` means "everything this run changed
    // could be undone", not "was". Reading it as the latter is the misreading this comment
    // exists to prevent.
    //
    // Nullable: runs recorded before this model existed have no honest value.
    public string? FinalState { get; set; }
    public int? ChangedCount { get; set; }
    public string? RollbackPlanJson { get; set; }

    // The strictest tier among the nodes this run EXECUTED, as a wire value.
    //
    // Recorded for one reader: the `subflow` step in a PARENT run. The parent cannot work this
    // out — it would have to re-resolve a graph it did not run, against snippet rows that may
    // have changed since — so the child records it on its way out.
    //
    // Skipped nodes are excluded, and a run in which nothing ran at all is `idempotent`: it
    // left no side effect to compensate. Null on runs recorded before this existed.
    public string? StrictestTier { get; set; }

    // Whether this run stops at a failure the graph does not say what to do about.
    //
    // Recorded on the run, not only read from it, for one reason: a reader looking at an old
    // run that carried on past a failure has to be able to tell "this ran under the previous
    // behaviour" from "someone asked for it". Null is the first of those.
    //
    // Null reads as TRUE at execution time. The default is the safe reading, and the column is
    // nullable only because runs recorded before it existed cannot honestly claim either.
    public bool? StopOnFailure { get; set; }

    // Immutable DAG captured at enqueue time. The worker reads the run from
    // these columns instead of re-loading the live workflow, so an edit that
    // lands between enqueue and execution cannot change what this run does.
    // Nullable only for rows created before the snapshot columns existed.
    public JsonElement NodesSnapshot { get; set; } = default;
    public JsonElement EdgesSnapshot { get; set; } = default;
}
