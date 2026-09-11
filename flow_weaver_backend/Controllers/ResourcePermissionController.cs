using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// CRUD over the resource-scoped role table. Mutations require the global
// Admin tier today; the next iteration will let resource-owners delegate
// without needing deployment-wide admin.
[ApiController]
[Route("api/permissions/{resourceType}/{resourceId:guid}")]
[HasPermission("access.read")]
public class ResourcePermissionController : ControllerBase
{
    private readonly IResourcePermissionService _service;

    public ResourcePermissionController(IResourcePermissionService service)
    {
        _service = service;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<IEnumerable<ResourcePermissionResponse>>> List(
        string resourceType, Guid resourceId, CancellationToken ct)
    {
        if (!ResourceTypes.IsKnown(resourceType))
            return Problems.BadRequest(
                $"unknown resource_type '{resourceType}'",
                code: "resource_type_unknown");
        var rows = await _service.ListAsync(resourceType, resourceId, ct);
        return Ok(rows);
    }

    [HttpPost]
    [HasPermission("access.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<ResourcePermissionResponse>> Grant(
        string resourceType, Guid resourceId,
        [FromBody] GrantResourcePermissionRequest dto, CancellationToken ct)
    {
        try
        {
            var row = await _service.GrantAsync(resourceType, resourceId, dto, ct);
            return Ok(row);
        }
        catch (ArgumentException ex)
        {
            return Problems.BadRequest(ex.Message, code: "permission_invalid");
        }
    }

    [HttpDelete("{permissionId:guid}")]
    [HasPermission("access.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<IActionResult> Revoke(
        string resourceType, Guid resourceId, Guid permissionId, CancellationToken ct)
    {
        await _service.RevokeAsync(permissionId, ct);
        return NoContent();
    }
}
