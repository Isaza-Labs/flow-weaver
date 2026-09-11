using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Routed at /api/run (singular) so the plan's convention of
// `IWorkflowRun` as the service pair stays internal while the HTTP surface
// reads cleanly: GET /api/run, GET /api/run/{id}, GET /api/run/{id}/steps.
[ApiController]
[Route("api/run")]
[HasPermission("run.read")]
public class RunController : ControllerBase
{
    private readonly IWorkflowRun _runs;
    private readonly IStepRun _steps;

    public RunController(IWorkflowRun runs, IStepRun steps)
    {
        _runs = runs;
        _steps = steps;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<WorkflowRunResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _runs.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<WorkflowRunResponse>> GetById(Guid id)
        => _runs.GetByIdAsync(id);

    // Nested list: /api/run/{runId}/steps. Kept here (instead of under
    // /api/steprun?workflow_run_id=...) because the parent-child URL matches
    // the frontend's existing shape and scopes the query naturally.
    [HttpGet("{runId:guid}/steps")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<StepRunResponse>>> GetSteps(
        Guid runId,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
        => _steps.GetByRunAsync(runId, limit, offset);

    // Operator-initiated stop for an in-flight run. Viewer-level on purpose:
    // anyone authorized to launch a run via the UI also needs to be able to
    // pull the cord on it.
    [HttpPost("{id:guid}/cancel")]
    [HasPermission("run.cancel")]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public Task<ActionResult<WorkflowRunResponse>> Cancel(Guid id) => _runs.CancelAsync(id);

    // Admin-only deletes. The engine is still the only writer during
    // execution; these exist so operators can prune the history from the
    // UI without hitting the database directly. Soft-deletes cascade to
    // step_runs so the monitor view clears consistently.
    [HttpDelete("{id:guid}")]
    [HasPermission("run.delete")]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public Task<ActionResult> Delete(Guid id) => _runs.DeleteAsync(id);

    [HttpDelete]
    [HasPermission("run.delete")]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public Task<ActionResult<object>> DeleteAll() => _runs.DeleteAllAsync();
}
