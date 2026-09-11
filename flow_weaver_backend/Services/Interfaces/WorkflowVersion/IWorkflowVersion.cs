using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

// Snapshots of a workflow at promotion time. Writes happen only through the
// promotion flow (Sprint 3) — the HTTP surface is read-only.
//
// List is always filtered by workflow_id: unrestricted listing would mix
// versions across workflows in the UI and bust any reasonable pagination.
public interface IWorkflowVersion
{
    Task<ActionResult<ListResponse<WorkflowVersionResponse>>> GetByWorkflowAsync(
        Guid workflowId, int limit = 50, int offset = 0);

    Task<ActionResult<WorkflowVersionResponse>> GetByIdAsync(Guid id);
}
