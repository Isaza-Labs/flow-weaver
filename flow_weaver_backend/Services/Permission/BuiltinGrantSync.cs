using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Permission.Catalog;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Services.Permission;

public sealed class BuiltinGrantSync : IBuiltinGrantSync
{
    public const string OperatorGrantName = "builtin.operator";
    public const string ViewerGrantName = "builtin.viewer";

    // legacy role → built-in grant name. admin is absent on purpose (bypass).
    private static readonly IReadOnlyDictionary<string, string> RoleToGrant =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operator"] = OperatorGrantName,
            ["viewer"] = ViewerGrantName,
        };

    private readonly AppDbContext _db;
    private readonly ILogger<BuiltinGrantSync> _logger;

    public BuiltinGrantSync(AppDbContext db, ILogger<BuiltinGrantSync> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task EnsureGrantsAsync(CancellationToken ct = default)
    {
        EnsureOne(
            await _db.PermissionGrants.FirstOrDefaultAsync(
                g => g.Name == ViewerGrantName, ct),
            ViewerGrantName, "viewer");
        EnsureOne(
            await _db.PermissionGrants.FirstOrDefaultAsync(
                g => g.Name == OperatorGrantName, ct),
            OperatorGrantName, "operator");

        await _db.SaveChangesAsync(ct);
    }

    private void EnsureOne(PermissionGrant? grant, string name, string legacyRole)
    {
        var caps = CapabilityCatalog.CapabilitiesForLegacyRole(legacyRole).ToList();
        var now = DateTime.UtcNow;

        if (grant is null)
        {
            _db.PermissionGrants.Add(new PermissionGrant
            {
                PermissionGrantId = Guid.NewGuid(),
                Name = name,
                Description = $"Built-in bundle reproducing the legacy '{legacyRole}' role. Managed automatically.",
                Enabled = true,
                IsBuiltIn = true,
                SubjectIds = new(),
                Capabilities = caps,
                Conditions = EmptyConditions(),
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            return;
        }

        // Refresh caps if the catalogue changed since this bundle was seeded,
        // so adding a capability in code propagates to an existing install.
        if (!grant.Capabilities.ToHashSet(StringComparer.Ordinal).SetEquals(caps))
        {
            grant.Capabilities = caps;
            grant.UpdatedAt = now;
        }
        if (!grant.IsBuiltIn || !grant.Enabled)
        {
            grant.IsBuiltIn = true;
            grant.Enabled = true;
            grant.UpdatedAt = now;
        }
    }

    public async Task SyncUserAsync(Guid userId, string role, CancellationToken ct = default)
    {
        await EnsureGrantsAsync(ct);

        // null target for admin / unknown role → membership of no built-in.
        RoleToGrant.TryGetValue(role ?? string.Empty, out var targetName);

        var grants = await _db.PermissionGrants
            .Where(g => g.IsBuiltIn)
            .ToListAsync(ct);

        var changed = false;
        foreach (var g in grants)
        {
            var shouldContain = g.Name == targetName;
            var contains = g.SubjectIds.Contains(userId);

            if (shouldContain && !contains)
            {
                // Reassign (not in-place mutate) so EF reliably detects the
                // array change regardless of the value comparer in play.
                g.SubjectIds = g.SubjectIds.Append(userId).ToList();
                g.UpdatedAt = DateTime.UtcNow;
                changed = true;
            }
            else if (!shouldContain && contains)
            {
                g.SubjectIds = g.SubjectIds.Where(x => x != userId).ToList();
                g.UpdatedAt = DateTime.UtcNow;
                changed = true;
            }
        }

        if (changed)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "permission.grants.sync_user user_id={UserId} role={Role} grant={Grant}",
                userId, role, targetName ?? "(none)");
        }
    }

    private static JsonElement EmptyConditions() => JsonDocument.Parse("{}").RootElement.Clone();
}
