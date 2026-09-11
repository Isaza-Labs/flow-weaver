using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

// Step-level execution records. A step_run is always a child of one
// workflow_run, so the primary list query filters by the parent run id —
// listing every step across every run would have no UI use and only leak
// execution volume to operators.
//
// GetByIdAsync still exists because deep links from logs/traces need to
// fetch a single step directly.
public interface IStepRun
{
    Task<ActionResult<ListResponse<StepRunResponse>>> GetByRunAsync(Guid workflowRunId, int limit = 50, int offset = 0);

    Task<ActionResult<StepRunResponse>> GetByIdAsync(Guid id);
}
