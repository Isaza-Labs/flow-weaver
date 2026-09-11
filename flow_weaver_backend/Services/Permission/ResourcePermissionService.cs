using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Permission;

public sealed class ResourcePermissionService : IResourcePermissionService
{
    private readonly IResourcePermissionRepository _perms;
    private readonly IUserRepository _users;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ILogger<ResourcePermissionService> _logger;

    public ResourcePermissionService(
        IResourcePermissionRepository perms,
        IUserRepository users,
        ICurrentUser caller,
        IAuditLogger audit,
        ILogger<ResourcePermissionService> logger)
    {
        _perms = perms;
        _users = users;
        _caller = caller;
        _audit = audit;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ResourcePermissionResponse>> ListAsync(
        string resourceType, Guid resourceId, CancellationToken ct)
    {
        if (!ResourceTypes.IsKnown(resourceType))
            throw new ArgumentException($"unknown resource_type '{resourceType}'", nameof(resourceType));

        var rows = await _perms.ListByResourceAsync(resourceType.ToLowerInvariant(), resourceId, ct);

        var subjectIds = rows.Select(r => r.SubjectId)
            .Concat(rows.Where(r => r.GrantedBy.HasValue).Select(r => r.GrantedBy!.Value))
            .Distinct()
            .ToList();
        var users = await _users.GetUsernamesByIdsAsync(subjectIds, ct);

        return rows.Select(r => new ResourcePermissionResponse
        {
            ResourcePermissionId = r.ResourcePermissionId,
            ResourceType = r.ResourceType,
            ResourceId = r.ResourceId,
            SubjectType = r.SubjectType,
            SubjectId = r.SubjectId,
            SubjectUsername = users.TryGetValue(r.SubjectId, out var su) ? su : null,
            Role = r.Role,
            GrantedBy = r.GrantedBy,
            GrantedByUsername = r.GrantedBy is { } gb && users.TryGetValue(gb, out var gu) ? gu : null,
            GrantedAt = r.GrantedAt,
        }).ToList();
    }

    public async Task<ResourcePermissionResponse> GrantAsync(
        string resourceType, Guid resourceId, GrantResourcePermissionRequest dto, CancellationToken ct)
    {
        if (!ResourceTypes.IsKnown(resourceType))
            throw new ArgumentException($"unknown resource_type '{resourceType}'", nameof(resourceType));
        if (ResourceRoles.Rank(dto.Role) < 0)
            throw new ArgumentException($"unknown role '{dto.Role}'", nameof(dto));
        if (!string.Equals(dto.SubjectType, "user", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("only subject_type=user is supported today", nameof(dto));
        if (dto.SubjectId == Guid.Empty)
            throw new ArgumentException("subject_id is required", nameof(dto));

        var rt = resourceType.ToLowerInvariant();
        var role = dto.Role.ToLowerInvariant();
        var subjectType = dto.SubjectType.ToLowerInvariant();

        // Idempotent: if the same grant already exists, return it.
        var existing = await _perms.FindActiveGrantAsync(rt, resourceId, subjectType, dto.SubjectId, role, ct);
        if (existing is not null)
            return await ToResponseAsync(existing, includeUsernames: true, ct);

        var now = DateTime.UtcNow;
        var entry = new ResourcePermission
        {
            ResourcePermissionId = Guid.NewGuid(),
            ResourceType = rt,
            ResourceId = resourceId,
            SubjectType = subjectType,
            SubjectId = dto.SubjectId,
            Role = role,
            GrantedBy = _caller.UserId,
            GrantedAt = now,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var (row, created) = await _perms.AddGrantResolvingRaceAsync(entry, ct);
        if (!created)
        {
            _logger.LogInformation(
                "permission.grant.race_resolved permission_id={PermissionId} resource_type={ResourceType} resource_id={ResourceId} subject_id={SubjectId} role={Role}",
                row.ResourcePermissionId, rt, resourceId, dto.SubjectId, role);
            return await ToResponseAsync(row, includeUsernames: true, ct);
        }

        await _audit.LogAsync("resource_permission", row.ResourcePermissionId, "grant",
            after: new { resource_type = rt, resource_id = resourceId, subject_id = dto.SubjectId, role });

        _logger.LogInformation(
            "permission.grant.ok resource_type={ResourceType} resource_id={ResourceId} subject_id={SubjectId} role={Role}",
            rt, resourceId, dto.SubjectId, role);

        return await ToResponseAsync(row, includeUsernames: true, ct);
    }

    public async Task RevokeAsync(Guid permissionId, CancellationToken ct)
    {
        var entry = await _perms.GetByIdAsync(permissionId, activeOnly: false, tracking: true, ct);
        if (entry is null || !entry.IsActive) return;

        entry.IsActive = false;
        entry.UpdatedAt = DateTime.UtcNow;
        await _perms.SaveChangesAsync(ct);

        await _audit.LogAsync("resource_permission", entry.ResourcePermissionId, "revoke",
            before: new
            {
                resource_type = entry.ResourceType,
                resource_id = entry.ResourceId,
                subject_id = entry.SubjectId,
                role = entry.Role,
            });

        _logger.LogInformation(
            "permission.revoke.ok permission_id={PermissionId}", permissionId);
    }

    public async Task<bool> HasAtLeastAsync(
        string resourceType, Guid resourceId, string requiredRole, CancellationToken ct)
    {
        if (!ResourceTypes.IsKnown(resourceType))
            return false;

        var requiredRank = ResourceRoles.Rank(requiredRole);
        if (requiredRank < 0) return false;

        // Global RBAC floor: admin always passes, operator passes up to
        // runner, viewer passes for viewer requirement.
        var roles = _caller.Roles ?? Array.Empty<string>();
        if (roles.Contains("admin", StringComparer.OrdinalIgnoreCase))
            return true;
        if (roles.Contains("operator", StringComparer.OrdinalIgnoreCase)
            && requiredRank <= ResourceRoles.Rank(ResourceRoles.Runner))
            return true;
        if (requiredRank <= ResourceRoles.Rank(ResourceRoles.Viewer))
        {
            // Authenticated-but-no-resource-grant viewers see resources
            // they have NOT been excluded from. Today this is a
            // simplification — the real model is "if any per-resource
            // grant exists for this resource, viewer must hold one;
            // otherwise global viewer is fine". Refine when the
            // product team has decided on a deny-by-default model.
            return _caller.IsAuthenticated;
        }

        // Anything stronger than viewer requires an explicit grant.
        var rt = resourceType.ToLowerInvariant();
        var heldRoles = await _perms.GetActiveRolesForSubjectAsync(rt, resourceId, _caller.UserId, ct);

        var max = heldRoles.Select(ResourceRoles.Rank).DefaultIfEmpty(-1).Max();
        return max >= requiredRank;
    }

    private async Task<ResourcePermissionResponse> ToResponseAsync(
        ResourcePermission entry, bool includeUsernames, CancellationToken ct)
    {
        string? subjectUsername = null;
        string? grantedByUsername = null;
        if (includeUsernames)
        {
            var ids = new List<Guid> { entry.SubjectId };
            if (entry.GrantedBy.HasValue) ids.Add(entry.GrantedBy.Value);
            var users = await _users.GetUsernamesByIdsAsync(ids, ct);
            users.TryGetValue(entry.SubjectId, out subjectUsername);
            if (entry.GrantedBy is { } gb)
                users.TryGetValue(gb, out grantedByUsername);
        }
        return new ResourcePermissionResponse
        {
            ResourcePermissionId = entry.ResourcePermissionId,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            SubjectType = entry.SubjectType,
            SubjectId = entry.SubjectId,
            SubjectUsername = subjectUsername,
            Role = entry.Role,
            GrantedBy = entry.GrantedBy,
            GrantedByUsername = grantedByUsername,
            GrantedAt = entry.GrantedAt,
        };
    }
}
