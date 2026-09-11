using System.Text.Json;
using JobModel = flow_weaver_backend.Models.Job;

namespace flow_weaver_backend.Services.Engine;

// The queue is the handshake between the workflow executor and the worker
// pool. All four methods run raw SQL via Dapper — EF Core's change tracker
// gets in the way of the FOR UPDATE SKIP LOCKED pattern, and the
// throughput-critical path is short enough that hand-written SQL is the
// clearest option.
//
// Job lifecycle: pending → claimed → completed | failed. Only the worker
// that claimed a job may transition it further; enforcement is by worker
// id in UPDATE WHERE clauses rather than application locks.
public interface IQueueRepository
{
    // Atomically pick the highest-priority pending job tagged for one of
    // the given worker tags and mark it claimed. Returns null when the
    // queue is empty — callers should back off (Task.Delay) rather than
    // spin.
    //
    // `tags` is the worker's tag list (default ["default"]); a job is
    // eligible only if its tag is in that set.
    Task<JobModel?> ClaimAsync(string[] tags, string workerId, CancellationToken ct);

    // Mark the given job completed. Sets completed_at to NOW(). Idempotent:
    // re-calling on an already-completed row is a no-op.
    Task CompleteAsync(Guid jobId, CancellationToken ct);

    // Mark the given job failed. The error string is currently swallowed
    // because the Job model has no error column — Sprint 2.4 surfaces step
    // failure detail via step_runs.error. The parameter is kept for future
    // use without breaking callers.
    Task FailAsync(Guid jobId, string error, CancellationToken ct);

    // Insert a new pending job. Returns the generated job id. The executor
    // uses this to enqueue one row per step in the DAG.
    Task<Guid> EnqueueAsync(
        string type,
        JsonElement payload,
        string tag,
        int priority,
        CancellationToken ct);

    // Sweep claims whose lease has expired back to 'pending'. Called on a
    // cadence by JobReclaimHostedService. Returns the number of jobs
    // released so operators can monitor crash recovery in logs.
    Task<int> ReclaimExpiredAsync(CancellationToken ct);

    // Push the lease forward on a still-running claim so reclaim doesn't
    // resurrect the job under a healthy worker. Used by long-running jobs
    // (notably workflow_run orchestration, which can outlive the 5-minute
    // default lease) to signal "still alive". Returns true if the row was
    // updated — false means the claim is gone (already reclaimed, completed,
    // or failed) and the caller should stop heartbeating.
    Task<bool> RenewLeaseAsync(Guid jobId, string workerId, int leaseSeconds, CancellationToken ct);

    // Drop every still-pending job that belongs to the given workflow_run
    // (the orchestrator job itself + any per-step jobs whose step_run row
    // points back at the run). Called from the cancel-run path so a worker
    // doesn't pick up a queued job after the operator has aborted. Claimed
    // jobs are left alone — the worker will finish or its lease will
    // expire — because killing them mid-flight would risk leaving the
    // remote system in a half-applied state.
    Task<int> CancelPendingByRunAsync(Guid workflowRunId, CancellationToken ct);
}
