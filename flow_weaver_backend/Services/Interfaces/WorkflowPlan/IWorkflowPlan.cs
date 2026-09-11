using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

// Standard CRUD (draft management) plus the governance transitions:
// submit → approve / reject → build. Each transition enforces the valid
// source status and is a deliberate separate endpoint so the audit trail
// reflects exactly who did what.
public interface IWorkflowPlan : IBaseService<WorkflowPlanResponse, CreateWorkflowPlan, UpdateWorkflowPlan>
{
    // draft → awaiting_approval
    Task<ActionResult<WorkflowPlanResponse>> SubmitForApprovalAsync(Guid id);

    // awaiting_approval → approved. Captures ApprovedBy + ApprovedAt.
    Task<ActionResult<WorkflowPlanResponse>> ApproveAsync(Guid id, ApprovePlan dto);

    // awaiting_approval → rejected. Captures RejectionReason + ApprovedBy.
    Task<ActionResult<WorkflowPlanResponse>> RejectAsync(Guid id, RejectPlan dto);

    // approved → building → built. Materializes the plan as a Workflow.
    // Until the workflow engine exists (Fase 3) this just flips status; the
    // actual workflow materialization is wired in when the engine lands.
    Task<ActionResult<WorkflowPlanResponse>> BuildAsync(Guid id);
}
