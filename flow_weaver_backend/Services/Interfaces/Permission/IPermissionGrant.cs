using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

// CRUD for granular permission grants plus subject (user) assignment. Extends
// the shared IBaseService so the controller stays a thin one-liner layer like
// PolicyController. Built-in grants are visible but rejected by the mutating
// members (they are owned by the seeder / legacy-role dual-write).
public interface IPermissionGrant
    : IBaseService<PermissionGrantResponse, CreatePermissionGrant, UpdatePermissionGrant>
{
    // Add/remove a user as a subject of a grant. Idempotent.
    Task<ActionResult<PermissionGrantResponse>> AddSubjectAsync(Guid id, Guid userId);
    Task<ActionResult<PermissionGrantResponse>> RemoveSubjectAsync(Guid id, Guid userId);
}
