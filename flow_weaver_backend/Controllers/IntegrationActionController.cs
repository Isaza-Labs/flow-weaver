using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[HasPermission("integrationaction.read")]
public class IntegrationActionController : ControllerBase
{
    private readonly IIntegrationAction _service;
    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;

    public IntegrationActionController(
        IIntegrationAction service,
        AppDbContext db,
        ICurrentUser caller)
    {
        _service = service;
        _db = db;
        _caller = caller;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<IntegrationActionResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<IntegrationActionResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [HasPermission("integrationaction.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<IntegrationActionResponse>> Post([FromBody] CreateIntegrationAction dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("integrationaction.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<IntegrationActionResponse>> Update(Guid id, [FromBody] UpdateIntegrationAction dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("integrationaction.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<IntegrationActionResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);

    // Cross-integration flat list used by the workflow editor's service
    // palette. Joins IntegrationAction with its parent Integration so the
    // UI can show `<integration.name> / <action.name>` without a second
    // round-trip.
    [HttpGet("actions/all")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<List<IntegrationActionPaletteItem>>> ListAllForPalette()
    {
        var rows = await (
            from act in _db.IntegrationActions.AsNoTracking()
            join integ in _db.Integrations.AsNoTracking()
                on act.IntegrationId equals integ.IntegrationId
            where act.IsActive
                  && integ.IsActive
                  && act.Enabled
            orderby integ.Name, act.Category, act.Name
            select new IntegrationActionPaletteItem
            {
                Id = act.IntegrationActionId,
                IntegrationId = integ.IntegrationId,
                IntegrationName = integ.Name,
                IntegrationType = integ.Type,
                Name = act.Name,
                Description = act.Description,
                Method = act.Method,
                Path = act.Path,
                PathParams = act.PathParams,
                QueryParams = act.QueryParams,
                RequestBody = act.RequestBody,
                Category = act.Category,
            }).ToListAsync();

        return Ok(rows);
    }
}
