using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Permission.Catalog;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Management API for granular permission grants.
// Admin-only: authoring/assigning access is an administration action. The
// grant-builder UI reads the capability catalogue from GET .../capabilities.
//
// NOTE: gated with the legacy Admin policy for now; phase 4 swaps this to
// [HasPermission("access.manage")] once the attribute + provider land.
[ApiController]
[Route("api/permission-grants")]
[Authorize(Policy = "Admin")]
public class PermissionGrantController : ControllerBase
{
    private readonly IPermissionGrant _service;

    public PermissionGrantController(IPermissionGrant service)
    {
        _service = service;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<PermissionGrantResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<PermissionGrantResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<PermissionGrantResponse>> Post([FromBody] CreatePermissionGrant dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<PermissionGrantResponse>> Update(Guid id, [FromBody] UpdatePermissionGrant dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<PermissionGrantResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);

    // Assignment: add/remove a user as a subject of a grant. Idempotent.
    [HttpPost("{id:guid}/subjects/{userId:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<PermissionGrantResponse>> AddSubject(Guid id, Guid userId)
        => _service.AddSubjectAsync(id, userId);

    [HttpDelete("{id:guid}/subjects/{userId:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<PermissionGrantResponse>> RemoveSubject(Guid id, Guid userId)
        => _service.RemoveSubjectAsync(id, userId);

    // The capability catalogue the grant-builder picks from. Static, code-defined.
    [HttpGet("capabilities")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public ActionResult<IEnumerable<CapabilityInfo>> Capabilities()
        => new OkObjectResult(CapabilityCatalog.All.Select(c => new CapabilityInfo
        {
            Key = c.Key,
            Domain = c.Domain,
            Description = c.Description,
            Conditionable = Dims(c.Conditionable),
        }));

    private static List<string> Dims(ContextDimensions d)
    {
        var list = new List<string>();
        if (d.HasFlag(ContextDimensions.Environment)) list.Add("environment");
        if (d.HasFlag(ContextDimensions.Device)) list.Add("device");
        if (d.HasFlag(ContextDimensions.Resource)) list.Add("resource");
        if (d.HasFlag(ContextDimensions.Mcp)) { list.Add("mcp_server"); list.Add("mcp_tool"); }
        return list;
    }
}
