using System.Text.Json;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// WorkflowRun lifecycle. Generic CRUD covers list/get; these bulk operations
// span the run's child step_runs and run as set-based ExecuteUpdate (no entity
// materialisation), so they belong in the data layer rather than the service.
public interface IWorkflowRunRepository : IRepository<WorkflowRun>
{
    // Recent completed runs for one workflow whose CompletedAt is at/after
    // `since`, newest first, capped at `limit`, projected to the fields the
    // acceptance-test grader compares against. Active-only. Read-only.
    Task<IReadOnlyList<CompletedRunRow>> ListRecentCompletedAsync(
        Guid workflowId, DateTime since, int limit, CancellationToken ct = default);

    // Soft-delete the still-active step_runs of one run. Returns affected count.
    Task<int> SoftDeleteStepsByRunAsync(Guid runId, DateTime now, CancellationToken ct = default);

    // Soft-delete every active run + step. Returns (runs, steps).
    Task<(int runs, int steps)> SoftDeleteAllAsync(DateTime now, CancellationToken ct = default);

    // Move the pending/running step_runs of one run to a terminal Cancelled
    // state. Returns affected count.
    Task<int> CancelInFlightStepsByRunAsync(Guid runId, DateTime now, CancellationToken ct = default);

    // Environment of the workflow behind a run (joins runs→workflows). The
    // worker resolves it from a trusted run id mid-dispatch.
    Task<string?> GetEnvironmentByRunIdAsync(Guid workflowRunId, CancellationToken ct = default);

    // The run's workflow id and creator — the report handler stamps both onto
    // artifacts so the admin artifacts view can filter by workflow AND
    // attribute the document to the user who triggered the run. Null when the
    // run does not exist.
    Task<RunOriginRow?> GetRunOriginByRunIdAsync(Guid workflowRunId, CancellationToken ct = default);
}

// Candidate-run projection the acceptance-test grader matches inputs against.
public sealed record CompletedRunRow(
    Guid WorkflowRunId, JsonElement InputPayload, string Status, DateTime? CompletedAt);

// Origin of a run: which workflow it belongs to and who triggered it.
// CreatedBy is the raw stored string (a user id for user/schedule-triggered
// runs); callers decide how to interpret it.
public sealed record RunOriginRow(Guid WorkflowId, string? CreatedBy);
