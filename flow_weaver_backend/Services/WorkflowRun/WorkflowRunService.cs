using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Services.WorkflowRun;

public class WorkflowRunService : IWorkflowRun
{
    private readonly IWorkflowRunRepository _runs;
    private readonly ICurrentUser _caller;
    private readonly IQueueRepository _queue;
    private readonly IAuditLogger _audit;
    private readonly ILogger<WorkflowRunService> _logger;

    public WorkflowRunService(
        IWorkflowRunRepository runs,
        ICurrentUser caller,
        IQueueRepository queue,
        IAuditLogger audit,
        ILogger<WorkflowRunService> logger)
    {
        _runs = runs;
        _caller = caller;
        _queue = queue;
        _audit = audit;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<WorkflowRunResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _runs.CountAsync();
        var runs = await _runs.ListAsync(limit, offset);

        _logger.LogDebug(
            "workflow_run.list.ok total={Total} returned={Returned}",
            total, runs.Count);

        return new OkObjectResult(new ListResponse<WorkflowRunResponse>
        {
            Data = runs.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<WorkflowRunResponse>> GetByIdAsync(Guid id)
    {
        var run = await _runs.GetByIdAsync(id, tracking: false);
        if (run is null)
        {
            _logger.LogWarning("workflow_run.get.not_found workflow_run_id={WorkflowRunId}", id);
            return new NotFoundObjectResult(new { error = "workflow_run not found" });
        }

        return ToResponse(run);
    }

    public async Task<ActionResult> DeleteAsync(Guid id)
    {
        var run = await _runs.GetByIdAsync(id);
        if (run is null)
        {
            _logger.LogWarning("workflow_run.delete.not_found workflow_run_id={WorkflowRunId}", id);
            return new NotFoundObjectResult(new { error = "workflow_run not found" });
        }

        var now = DateTime.UtcNow;
        run.IsActive = false;
        run.UpdatedAt = now;

        // Cascade soft-delete the child step_runs so they stop appearing in
        // the monitor/timeline queries that filter on IsActive.
        await _runs.SoftDeleteStepsByRunAsync(id, now);

        await _runs.SaveChangesAsync();

        _logger.LogInformation("workflow_run.delete.ok workflow_run_id={WorkflowRunId}", id);
        return new NoContentResult();
    }

    public async Task<ActionResult<object>> DeleteAllAsync()
    {
        var now = DateTime.UtcNow;

        var (runs, steps) = await _runs.SoftDeleteAllAsync(now);

        _logger.LogInformation(
            "workflow_run.delete_all.ok runs={Runs} steps={Steps}",
            runs, steps);

        return new OkObjectResult(new { deleted = runs, steps_deleted = steps });
    }

    public async Task<ActionResult<WorkflowRunResponse>> CancelAsync(Guid id)
    {
        var run = await _runs.GetByIdAsync(id);
        if (run is null)
        {
            _logger.LogWarning("workflow_run.cancel.not_found workflow_run_id={WorkflowRunId}", id);
            return new NotFoundObjectResult(new { error = "workflow_run not found" });
        }

        // Cancel is only meaningful while the run is still in flight.
        // Terminal runs are returned 409 so the UI can refresh and show the
        // already-final status instead of pretending we did something.
        if (run.Status != RunStatus.Pending && run.Status != RunStatus.Running)
        {
            _logger.LogWarning(
                "workflow_run.cancel.invalid_state workflow_run_id={WorkflowRunId} status={Status}",
                id, run.Status);
            return new ConflictObjectResult(new
            {
                error = $"run is already {run.Status} — cannot cancel",
                status = run.Status,
            });
        }

        var now = DateTime.UtcNow;
        var previousStatus = run.Status;

        run.Status = RunStatus.Cancelled;
        run.CompletedAt = now;
        run.UpdatedAt = now;

        // Stop any step_runs that haven't reached a terminal state. Workers
        // that are mid-flight on a claimed step won't see this until they
        // finish, but the orchestrator's polling loop will refuse to advance
        // the DAG once the parent run is cancelled.
        var stepsCancelled = await _runs.CancelInFlightStepsByRunAsync(id, now);

        await _runs.SaveChangesAsync();

        // Drop pending queue jobs for this run so workers don't keep picking
        // them up after the user has aborted. In-flight (claimed) jobs are
        // left alone — the worker will finish or time out, but the
        // orchestrator won't advance regardless because the run is now
        // cancelled.
        var jobsCancelled = await _queue.CancelPendingByRunAsync(id, CancellationToken.None);

        await _audit.LogAsync("workflow_run", id, "cancel",
            before: new { status = previousStatus },
            after: new { status = RunStatus.Cancelled, steps_cancelled = stepsCancelled, jobs_cancelled = jobsCancelled });

        _logger.LogInformation(
            "workflow_run.cancel.ok workflow_run_id={WorkflowRunId} steps_cancelled={Steps} jobs_cancelled={Jobs}",
            id, stepsCancelled, jobsCancelled);

        return ToResponse(run);
    }

    private static WorkflowRunResponse ToResponse(WorkflowRunModel r) => new()
    {
        Id = r.WorkflowRunId,
        WorkflowId = r.WorkflowId,
        Status = r.Status,
        InputPayload = r.InputPayload,
        TargetDevices = r.TargetDevices,
        TargetPools = r.TargetPools,
        Trigger = r.Trigger,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        // The run-detail page reads this to explain a failure no step owns.
        Error = r.Error,
        CreatedBy = r.CreatedBy,
        CreatedAt = r.CreatedAt,
        FinalState = r.FinalState,
        ChangedCount = r.ChangedCount,
        // Stored as JSON on the row; a plan that will not parse is reported as absent
        // rather than as an empty plan, which would read as "nothing to undo".
        RollbackPlan = ParseRollbackPlan(r.RollbackPlanJson),
    };

    private static IReadOnlyList<string>? ParseRollbackPlan(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
