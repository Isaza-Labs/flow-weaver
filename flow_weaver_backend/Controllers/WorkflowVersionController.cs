using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Read-only exposure of workflow version snapshots. The list endpoint
// requires workflow_id because unrestricted listing mixes versions across
// workflows and has no UI use case.
[ApiController]
[Route("api/workflowversion")]
[HasPermission("workflow.read")]
public class WorkflowVersionController : ControllerBase
{
    private readonly IWorkflowVersion _service;

    public WorkflowVersionController(IWorkflowVersion service)
    {
        _service = service;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<ListResponse<WorkflowVersionResponse>>> GetByWorkflow(
        [FromQuery(Name = "workflow_id")] Guid workflowId,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        if (workflowId == Guid.Empty)
            return Problems.BadRequest("workflow_id is required", code: "workflow_id_required");

        return await _service.GetByWorkflowAsync(workflowId, limit, offset);
    }

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<WorkflowVersionResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);
}
