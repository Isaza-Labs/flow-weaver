using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Permission.Catalog;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Permission;

// CRUD for permission grants,
// authored "like a policy" (name + capability picker + condition builder) and
// assigned to users. Thin layer over IRepository<PermissionGrant>; the resolver
// (IEffectivePermissions) is what actually reads these at enforcement time.
//
// Two invariants protect the built-in bundles: admins cannot create names in
// the reserved "builtin." namespace, and the mutating members refuse any grant
// with IsBuiltIn=true (those are owned by the seeder / legacy-role dual-write).
public class PermissionGrantService : IPermissionGrant
{
    private const string BuiltinPrefix = "builtin.";

    private readonly IRepository<PermissionGrant> _grants;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ILogger<PermissionGrantService> _logger;

    public PermissionGrantService(
        IRepository<PermissionGrant> grants, ICurrentUser caller,
        IAuditLogger audit, ILogger<PermissionGrantService> logger)
    {
        _grants = grants;
        _caller = caller;
        _audit = audit;
        _logger = logger;
    }

    // Shared audit projection. A grant IS the authorization decision, so the
    // capability list and the condition object go in whole — a diff of those
    // two fields is the difference between "can read devices in the lab" and
    // "can push config to production", and that is precisely the question an
    // auditor arrives with. ResourcePermission was already audited; this
    // sibling, which is the broader of the two, was not.
    private static object GrantAudit(PermissionGrant g) => new
    {
        g.Name,
        g.Description,
        g.Enabled,
        capabilities = g.Capabilities,
        conditions = g.Conditions,
        subject_ids = g.SubjectIds,
    };

    public async Task<ActionResult<ListResponse<PermissionGrantResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var total = await _grants.CountAsync();
        var rows = await _grants.ListAsync(limit, offset);

        return new OkObjectResult(new ListResponse<PermissionGrantResponse>
        {
            Data = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<PermissionGrantResponse>> GetByIdAsync(Guid id)
    {
        var row = await _grants.GetByIdAsync(id);
        return row is null ? NotFound(id) : ToResponse(row);
    }

    public async Task<ActionResult<PermissionGrantResponse>> PostAsync(CreatePermissionGrant dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            return Bad("name is required");
        if (dto.Name.Trim().StartsWith(BuiltinPrefix, StringComparison.OrdinalIgnoreCase))
            return Bad($"'{BuiltinPrefix}' is a reserved name prefix");

        if (!TryNormalizeCapabilities(dto.Capabilities, out var caps, out var capsError))
            return Bad(capsError);
        if (!TryNormalizeConditions(dto.Conditions, out var conditions, out var condError))
            return Bad(condError);

        var now = DateTime.UtcNow;
        var row = new PermissionGrant
        {
            PermissionGrantId = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            Description = dto.Description,
            Enabled = dto.Enabled,
            IsBuiltIn = false,                      // admins never mint built-ins
            Capabilities = caps,
            SubjectIds = dto.SubjectIds.Distinct().ToList(),
            Conditions = conditions,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _grants.Add(row);
        await _grants.SaveChangesAsync();

        await _audit.LogAsync("permission_grant", row.PermissionGrantId, "create",
            after: GrantAudit(row));

        _logger.LogInformation(
            "permission_grant.create.ok grant_id={GrantId} name={Name} caps={CapCount} subjects={SubjectCount}",
            row.PermissionGrantId, row.Name, caps.Count, row.SubjectIds.Count);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "PermissionGrant",
            routeValues: new { id = row.PermissionGrantId },
            value: ToResponse(row));
    }

    public async Task<ActionResult<PermissionGrantResponse>> UpdateAsync(Guid id, UpdatePermissionGrant dto)
    {
        var row = await _grants.GetByIdAsync(id);
        if (row is null) return NotFound(id);
        if (row.IsBuiltIn) return BuiltinReadonly();

        // Snapshot before the dto lands (`row` is tracked).
        var auditBefore = GrantAudit(row);

        if (dto.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return Bad("name cannot be blank");
            if (dto.Name.Trim().StartsWith(BuiltinPrefix, StringComparison.OrdinalIgnoreCase))
                return Bad($"'{BuiltinPrefix}' is a reserved name prefix");
            row.Name = dto.Name.Trim();
        }
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;
        if (dto.Capabilities is not null)
        {
            if (!TryNormalizeCapabilities(dto.Capabilities, out var caps, out var capsError))
                return Bad(capsError);
            row.Capabilities = caps;
        }
        if (dto.SubjectIds is not null)
            row.SubjectIds = dto.SubjectIds.Distinct().ToList();
        if (dto.Conditions is not null)
        {
            if (!TryNormalizeConditions(dto.Conditions.Value, out var conditions, out var condError))
                return Bad(condError);
            row.Conditions = conditions;
        }

        row.UpdatedAt = DateTime.UtcNow;
        await _grants.SaveChangesAsync();

        await _audit.LogAsync("permission_grant", row.PermissionGrantId, "update",
            before: auditBefore, after: GrantAudit(row));

        _logger.LogInformation("permission_grant.update.ok grant_id={GrantId}", row.PermissionGrantId);
        return ToResponse(row);
    }

    public async Task<ActionResult<PermissionGrantResponse>> DeleteAsync(Guid id)
    {
        var row = await _grants.GetByIdAsync(id);
        if (row is null) return NotFound(id);
        if (row.IsBuiltIn) return BuiltinReadonly();

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _grants.SaveChangesAsync();

        await _audit.LogAsync("permission_grant", row.PermissionGrantId, "delete",
            before: GrantAudit(row));

        _logger.LogInformation("permission_grant.delete.ok grant_id={GrantId}", row.PermissionGrantId);
        return ToResponse(row);
    }

    public async Task<ActionResult<PermissionGrantResponse>> AddSubjectAsync(Guid id, Guid userId)
    {
        var row = await _grants.GetByIdAsync(id);
        if (row is null) return NotFound(id);
        if (row.IsBuiltIn) return BuiltinReadonly();

        if (!row.SubjectIds.Contains(userId))
        {
            row.SubjectIds = row.SubjectIds.Append(userId).ToList();
            row.UpdatedAt = DateTime.UtcNow;
            await _grants.SaveChangesAsync();

            // Its own action: adding a user to a grant is how privilege is
            // actually handed out, and it must be findable without diffing
            // subject_ids arrays.
            await _audit.LogAsync("permission_grant", row.PermissionGrantId, "permission_grant.subject_added",
                after: new { grant_name = row.Name, user_id = userId, capabilities = row.Capabilities });

            _logger.LogInformation(
                "permission_grant.subject.add grant_id={GrantId} user_id={UserId}", row.PermissionGrantId, userId);
        }
        return ToResponse(row);
    }

    public async Task<ActionResult<PermissionGrantResponse>> RemoveSubjectAsync(Guid id, Guid userId)
    {
        var row = await _grants.GetByIdAsync(id);
        if (row is null) return NotFound(id);
        if (row.IsBuiltIn) return BuiltinReadonly();

        if (row.SubjectIds.Contains(userId))
        {
            row.SubjectIds = row.SubjectIds.Where(x => x != userId).ToList();
            row.UpdatedAt = DateTime.UtcNow;
            await _grants.SaveChangesAsync();

            await _audit.LogAsync("permission_grant", row.PermissionGrantId, "permission_grant.subject_removed",
                before: new { grant_name = row.Name, user_id = userId, capabilities = row.Capabilities });

            _logger.LogInformation(
                "permission_grant.subject.remove grant_id={GrantId} user_id={UserId}", row.PermissionGrantId, userId);
        }
        return ToResponse(row);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static bool TryNormalizeCapabilities(
        List<string> input, out List<string> normalized, out string error)
    {
        normalized = input
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        error = string.Empty;

        var unknown = normalized.Where(c => !CapabilityCatalog.IsKnown(c)).ToList();
        if (unknown.Count > 0)
        {
            error = $"unknown capabilities: {string.Join(", ", unknown)}";
            return false;
        }
        return true;
    }

    private static bool TryNormalizeConditions(JsonElement input, out JsonElement conditions, out string error)
    {
        error = string.Empty;
        // Undefined (omitted) → empty object = unconditional.
        if (input.ValueKind == JsonValueKind.Undefined)
        {
            conditions = JsonDocument.Parse("{}").RootElement.Clone();
            return true;
        }
        if (input.ValueKind != JsonValueKind.Object)
        {
            conditions = default;
            error = "conditions must be a JSON object";
            return false;
        }
        conditions = input.Clone();
        return true;
    }

    private static ActionResult<PermissionGrantResponse> Bad(string error) =>
        new BadRequestObjectResult(new { error });

    private ActionResult<PermissionGrantResponse> NotFound(Guid id)
    {
        _logger.LogWarning("permission_grant.not_found grant_id={GrantId}", id);
        return new NotFoundObjectResult(new { error = "permission grant not found" });
    }

    private static ActionResult<PermissionGrantResponse> BuiltinReadonly() =>
        new BadRequestObjectResult(new
        {
            error = "built-in grants are managed automatically and cannot be modified",
            code = "builtin_readonly",
        });

    internal static PermissionGrantResponse ToResponse(PermissionGrant row) => new()
    {
        PermissionGrantId = row.PermissionGrantId,
        Name = row.Name,
        Description = row.Description,
        Enabled = row.Enabled,
        IsBuiltIn = row.IsBuiltIn,
        Capabilities = row.Capabilities,
        SubjectIds = row.SubjectIds,
        Conditions = row.Conditions,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };
}
