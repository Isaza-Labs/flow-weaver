using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

// Runtime entity, read-only from the HTTP surface. The engine (Sprint 2.4+)
// is the only writer — it creates workflow_run rows when a run is enqueued
// and updates status/started_at/completed_at as the DAG progresses.
//
// Clients use this to build the runs list + detail view; the pairing step
// list is served via IStepRun.GetByRunAsync so the two concerns stay apart.
public interface IWorkflowRun
{
    Task<ActionResult<ListResponse<WorkflowRunResponse>>> GetAsync(int limit = 50, int offset = 0);

    Task<ActionResult<WorkflowRunResponse>> GetByIdAsync(Guid id);

    Task<ActionResult> DeleteAsync(Guid id);

    Task<ActionResult<object>> DeleteAllAsync();

    // Operator-initiated stop. Transitions a pending/running run to
    // 'cancelled', marks any in-flight step_runs cancelled, and removes
    // the run's still-pending queue jobs so workers don't pick them up
    // after the user has decided to abort.
    Task<ActionResult<WorkflowRunResponse>> CancelAsync(Guid id);
}
