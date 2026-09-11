using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Promotion;

public interface IPromotionService
{
    Task<ActionResult<WorkflowResponse>> PromoteAsync(Guid workflowId, PromoteRequest req, CancellationToken ct);
    Task<ActionResult<WorkflowResponse>> RollbackAsync(Guid workflowId, int toVersion, CancellationToken ct);
    Task<ActionResult<DiffResult>> DiffAsync(Guid workflowId, CancellationToken ct);
    Task<ActionResult<WorkflowResponse>> CloneAsync(Guid workflowId, CancellationToken ct);
}
