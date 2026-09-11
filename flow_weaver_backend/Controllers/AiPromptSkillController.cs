using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Seed;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Admin-only CRUD for the prompt skill rows. Frontend /skills
// page reads the uploaded .md file client-side and POSTs the content as
// JSON. Writes invalidate the SkillPromptLoader cache so the next
// /api/ai/catalog/prompt call reflects the new state immediately.
[ApiController]
[Route("api/admin/prompt-skills")]
[Authorize(Policy = "Admin")]
public class AiPromptSkillController : ControllerBase
{
    private readonly IAiPromptSkill _service;
    private readonly CatalogReseedService _reseed;

    public AiPromptSkillController(IAiPromptSkill service, CatalogReseedService reseed)
    {
        _service = service;
        _reseed = reseed;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<AiPromptSkillResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<AiPromptSkillResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AiPromptSkillResponse>> Post([FromBody] CreateAiPromptSkill dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AiPromptSkillResponse>> Update(Guid id, [FromBody] UpdateAiPromptSkill dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AiPromptSkillResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);

    // Force re-import of every .md file shipped under /Skills, upserting
    // rows by filename. Content that exists on disk overwrites the DB
    // version; custom skills (filenames not on disk) are left intact.
    // Use after a deploy that updates the bundled skill set.
    [HttpPost("reseed")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<ReseedResult>> Reseed(CancellationToken ct)
        => Ok(await _reseed.ReseedSkillsAsync(ct));
}
