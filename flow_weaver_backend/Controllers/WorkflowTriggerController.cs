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
[HasPermission("trigger.read")]
public class WorkflowTriggerController : ControllerBase
{
    private readonly IWorkflowTrigger _service;

    public WorkflowTriggerController(IWorkflowTrigger service)
    {
        _service = service;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<WorkflowTriggerResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<WorkflowTriggerResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [HasPermission("trigger.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowTriggerResponse>> Post([FromBody] CreateWorkflowTrigger dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("trigger.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowTriggerResponse>> Update(Guid id, [FromBody] UpdateWorkflowTrigger dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("trigger.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowTriggerResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);

    // Rotate a webhook trigger's signing secret. Returns the new plaintext
    // ONCE in `webhook_secret` — invalidates the previous secret immediately.
    [HttpPost("{id:guid}/rotate-secret")]
    [HasPermission("trigger.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<WorkflowTriggerResponse>> RotateSecret(Guid id)
        => _service.RotateWebhookSecretAsync(id);
}
