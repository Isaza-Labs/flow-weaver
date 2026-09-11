using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[HasPermission("integration.read")]
public class IntegrationController : ControllerBase
{
    private readonly IIntegration _service;
    private readonly IIntegrationAction _actions;
    private readonly IIntegrationHealthChecker _health;
    private readonly IIntegrationBundleService _bundle;
    private readonly IIntegrationCatalogService _catalog;

    public IntegrationController(
        IIntegration service,
        IIntegrationAction actions,
        IIntegrationHealthChecker health,
        IIntegrationBundleService bundle,
        IIntegrationCatalogService catalog)
    {
        _service = service;
        _actions = actions;
        _health = health;
        _bundle = bundle;
        _catalog = catalog;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<IntegrationResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<IntegrationResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [HasPermission("integration.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<IntegrationResponse>> Post([FromBody] CreateIntegration dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("integration.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<IntegrationResponse>> Update(Guid id, [FromBody] UpdateIntegration dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("integration.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<IntegrationResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);

    [HttpPost("{integrationId:guid}/actions")]
    [HasPermission("integration.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<IntegrationActionResponse>> CreateAction(
        Guid integrationId, [FromBody] CreateIntegrationAction dto)
        => _actions.PostForIntegrationAsync(integrationId, dto);

    // The scoped skills + specs currently linked to this integration, so the
    // /integrations view can list, select and edit them in place.
    [HttpGet("{id:guid}/bundle")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<IntegrationBundleView>> GetBundle(Guid id, CancellationToken ct)
        => _catalog.GetBundleAsync(id, ct);

    // Attach/replace an OpenAPI spec on this integration and re-materialize its
    // actions from the spec's operations (upsert-by-name). This is how loading
    // a spec updates the Actions list without re-creating the integration.
    [HttpPost("{id:guid}/specs")]
    [HasPermission("integration.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AttachSpecResult>> AttachSpec(
        Guid id, [FromBody] AttachSpecRequest dto, CancellationToken ct)
        => _catalog.AttachSpecAsync(id, dto, ct);

    // Attach/replace a prompt skill on this integration (upsert-by-name).
    [HttpPost("{id:guid}/skills")]
    [HasPermission("integration.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AiPromptSkillResponse>> AttachSkill(
        Guid id, [FromBody] AttachSkillRequest dto, CancellationToken ct)
        => _catalog.AttachSkillAsync(id, dto, ct);

    // Probes the upstream with one GET against BaseURL + HealthCheck.Path
    // and stores the outcome on the integration row. Operator-only because
    // it hits an external system.
    [HttpPost("{id:guid}/health")]
    [HasPermission("integration.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<IntegrationHealthCheckResult>> Health(Guid id, CancellationToken ct)
        => _health.CheckAsync(id, ct);

    // POST /api/integration/bundle — creates an Integration plus any number
    // of AiPromptSkill / AiApiSpec rows associated with it, all in a single
    // DB transaction. If any piece fails (validation or persistence), the
    // whole operation rolls back and no rows persist.
    //
    // Re-upload semantics mirror AiApiSpecService / AiPromptSkillService:
    //   - A row with the same (Name|Api) that is IsActive=false
    //     (soft-deleted, typically from an earlier seed or manual delete)
    //     gets REACTIVATED in place: content/order/integration_id replaced,
    //     IsActive flipped back to true.
    //   - A row that is IsActive=true returns 409 — the user must rename or
    //     delete it from /ai/skills or /ai/specs first.
    //
    // This is necessary because the unique index `IX_ai_*_(Name|Api)`
    // spans BOTH active and soft-deleted rows; without the reactivate path,
    // the INSERT collides with the dangling tombstone.
    [HttpPost("bundle")]
    [HasPermission("integration.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<CreateIntegrationBundleResult>> Bundle(
        [FromBody] CreateIntegrationBundle dto,
        CancellationToken ct)
    {
        var result = await _bundle.CreateAsync(dto, ct);
        return CreatedAtAction(nameof(GetById),
            new { id = result.Integration.IntegrationId }, result);
    }
}
