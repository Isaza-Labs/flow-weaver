using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Seed;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Admin-only CRUD for the OpenAPI spec rows. Every write
// re-parses the YAML, so a malformed spec fails validation instead of
// being persisted. The YamlSpecIndex is rebuilt after each write so the
// agent's discover/execute tools see the new operations immediately.
[ApiController]
[Route("api/admin/api-specs")]
[Authorize(Policy = "Admin")]
public class AiApiSpecController : ControllerBase
{
    private readonly IAiApiSpec _service;
    private readonly CatalogReseedService _reseed;

    public AiApiSpecController(IAiApiSpec service, CatalogReseedService reseed)
    {
        _service = service;
        _reseed = reseed;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<AiApiSpecResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<AiApiSpecResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AiApiSpecResponse>> Post([FromBody] CreateAiApiSpec dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AiApiSpecResponse>> Update(Guid id, [FromBody] UpdateAiApiSpec dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AiApiSpecResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);

    // Force re-import of every .yaml/.yml file shipped under /Specs,
    // upserting rows by api (filename stem). Use after deploying updated
    // bundled specs — the agent picks up the changes immediately because
    // the YamlSpecIndex is reloaded inline.
    [HttpPost("reseed")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<ReseedResult>> Reseed(CancellationToken ct)
        => Ok(await _reseed.ReseedSpecsAsync(ct));
}
