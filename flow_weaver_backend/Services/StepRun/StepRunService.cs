using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using StepRunModel = flow_weaver_backend.Models.StepRun;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Services.StepRun;

public class StepRunService : IStepRun
{
    private readonly IStepRunRepository _steps;
    private readonly IRepository<WorkflowRunModel> _runs;
    private readonly ICurrentUser _caller;
    private readonly ILogger<StepRunService> _logger;

    public StepRunService(
        IStepRunRepository steps,
        IRepository<WorkflowRunModel> runs,
        ICurrentUser caller,
        ILogger<StepRunService> logger)
    {
        _steps = steps;
        _runs = runs;
        _caller = caller;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<StepRunResponse>>> GetByRunAsync(
        Guid workflowRunId, int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        // Verify the parent run exists before returning any step data, so a
        // request for an unknown run reports the run as missing rather than
        // an empty step list.
        var runExists = await _runs.ExistsAsync(workflowRunId);
        if (!runExists)
        {
            _logger.LogWarning(
                "step_run.list.not_found workflow_run_id={WorkflowRunId} reason=parent_run_missing",
                workflowRunId);
            return new NotFoundObjectResult(new { error = "workflow_run not found" });
        }

        var total = await _steps.CountByRunAsync(workflowRunId);
        var steps = await _steps.ListByRunAsync(workflowRunId, limit, offset);

        _logger.LogDebug(
            "step_run.list.ok workflow_run_id={WorkflowRunId} total={Total} returned={Returned}",
            workflowRunId, total, steps.Count);

        return new OkObjectResult(new ListResponse<StepRunResponse>
        {
            Data = steps.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<StepRunResponse>> GetByIdAsync(Guid id)
    {
        var step = await _steps.GetByIdAsync(id, tracking: false);
        if (step is null)
        {
            _logger.LogWarning("step_run.get.not_found step_run_id={StepRunId}", id);
            return new NotFoundObjectResult(new { error = "step_run not found" });
        }

        return ToResponse(step);
    }

    private static StepRunResponse ToResponse(StepRunModel s) => new()
    {
        Id = s.StepRunId,
        WorkflowRunId = s.WorkflowRunId,
        NodeId = s.NodeId,
        SnippetId = s.SnippetId,
        DeviceId = s.DeviceId,
        Status = s.Status,
        InputPayload = s.InputPayload,
        OutputPayload = s.OutputPayload,
        Logs = s.Logs,
        Error = s.Error,
        StartedAt = s.StartedAt,
        CompletedAt = s.CompletedAt,
        WorkerId = s.WorkerId,
        Changed = s.ChangedState,
    };
}
