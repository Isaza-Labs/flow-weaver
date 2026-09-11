using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Permissions;
using flow_weaver_backend.Services.Ai.Tools;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[HasPermission("aiagent.read")]
public class AIAgentController : ControllerBase
{
    private readonly IAIAgent _service;

    public AIAgentController(IAIAgent service)
    {
        _service = service;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<AIAgentResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<AIAgentResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    // GET /api/aiagent/tools — the runtime tool catalog for the allowlist
    // picker. Names come from the live ToolRegistry (the same registry chat
    // dispatch resolves against), so the UI can offer exactly what exists on
    // this server instead of a free-text field prone to typos. Domain/tier are
    // joined from the PermissionClassifier matrix when the tool is classified
    // there (null for unclassified tools — they still exist and are selectable).
    [HttpGet("tools")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public IActionResult Tools([FromServices] ToolRegistry registry) =>
        Ok(registry.All
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t =>
            {
                PermissionClassifier.Matrix.TryGetValue(t.Name, out var perm);
                return new
                {
                    name = t.Name,
                    description = t.Description,
                    domain = perm?.Domain,
                    tier = perm?.Tier,
                };
            }));

    [HttpPost]
    [HasPermission("aiagent.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AIAgentResponse>> Post([FromBody] CreateAIAgent dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("aiagent.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AIAgentResponse>> Update(Guid id, [FromBody] UpdateAIAgent dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("aiagent.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AIAgentResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);
}
