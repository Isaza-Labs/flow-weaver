using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[HasPermission("plan.read")]
public class WorkflowPlanController : ControllerBase
{
    private readonly IWorkflowPlan _service;

    public WorkflowPlanController(IWorkflowPlan service)
    {
        _service = service;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<WorkflowPlanResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<WorkflowPlanResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [HasPermission("plan.create")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowPlanResponse>> Post([FromBody] CreateWorkflowPlan dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("plan.create")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowPlanResponse>> Update(Guid id, [FromBody] UpdateWorkflowPlan dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("plan.create")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowPlanResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);

    // ─── Governance transitions (admin-only for approve/reject/build) ──

    // Submit is operator-level: an operator can submit their own plan
    // for review, just like a pull-request author.
    [HttpPost("{id:guid}/submit")]
    [HasPermission("plan.submit")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowPlanResponse>> Submit(Guid id)
        => _service.SubmitForApprovalAsync(id);

    // approve/reject/build are admin-only — the governance gate.
    [HttpPost("{id:guid}/approve")]
    [HasPermission("plan.approve")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowPlanResponse>> Approve(Guid id, [FromBody] ApprovePlan dto)
        => _service.ApproveAsync(id, dto);

    [HttpPost("{id:guid}/reject")]
    [HasPermission("plan.approve")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowPlanResponse>> Reject(Guid id, [FromBody] RejectPlan dto)
        => _service.RejectAsync(id, dto);

    [HttpPost("{id:guid}/build")]
    [HasPermission("plan.build")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowPlanResponse>> Build(Guid id)
        => _service.BuildAsync(id);
}
