using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Per-resource RBAC grants. Generic CRUD covers add/get/save; these add the
// resource/subject-scoped lookups and the race-safe grant insert (the partial
// unique index can collide under concurrent grants).
public interface IResourcePermissionRepository : IRepository<ResourcePermission>
{
    // Active grants on a single resource.
    Task<IReadOnlyList<ResourcePermission>> ListByResourceAsync(
        string resourceType, Guid resourceId, CancellationToken ct = default);

    // The exact active grant (resource, subject, role), or null — the
    // idempotency fast-path.
    Task<ResourcePermission?> FindActiveGrantAsync(
        string resourceType, Guid resourceId,
        string subjectType, Guid subjectId, string role, CancellationToken ct = default);

    // Active role strings a user holds on a resource (for the HasAtLeast rank check).
    Task<IReadOnlyList<string>> GetActiveRolesForSubjectAsync(
        string resourceType, Guid resourceId, Guid subjectId, CancellationToken ct = default);

    // Insert a grant; on the unique-index race, detach and return the row that
    // won. Returns (row, created) — created=false means a concurrent insert won.
    Task<(ResourcePermission row, bool created)> AddGrantResolvingRaceAsync(
        ResourcePermission entry, CancellationToken ct = default);
}
