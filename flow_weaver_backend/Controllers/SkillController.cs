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
[HasPermission("skill.read")]
public class SkillController : ControllerBase
{
    private readonly ISkill _service;

    public SkillController(ISkill service)
    {
        _service = service;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<SkillResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<SkillResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [HasPermission("skill.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<SkillResponse>> Post([FromBody] CreateSkill dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("skill.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<SkillResponse>> Update(Guid id, [FromBody] UpdateSkill dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("skill.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<SkillResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);
}
