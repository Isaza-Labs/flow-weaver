using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

public interface IWorkflowTrigger : IBaseService<WorkflowTriggerResponse, CreateWorkflowTrigger, UpdateWorkflowTrigger>
{
    Task<ActionResult<ListResponse<WorkflowTriggerResponse>>> GetByWorkflowAsync(
        Guid workflowId, int limit = 50, int offset = 0);

    // Triggers are always scoped to a workflow, so creation lives under
    // POST /api/workflow/{workflowId}/triggers. The flat PostAsync (inherited)
    // returns 400 pointing callers here.
    Task<ActionResult<WorkflowTriggerResponse>> PostForWorkflowAsync(Guid workflowId, CreateWorkflowTrigger dto);

    // Generate a fresh webhook signing secret for a webhook-type trigger,
    // returning the plaintext ONCE in the response (WebhookSecret). Invalidates
    // the previous secret. 400 for non-webhook triggers, 404 if missing.
    Task<ActionResult<WorkflowTriggerResponse>> RotateWebhookSecretAsync(Guid id);
}
