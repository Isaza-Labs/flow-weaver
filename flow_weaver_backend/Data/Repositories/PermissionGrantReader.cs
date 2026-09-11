using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public sealed class PermissionGrantReader(AppDbContext db) : IPermissionGrantReader
{
    public async Task<IReadOnlyList<PermissionGrantRow>> GetActiveGrantsForSubjectAsync(
        Guid userId, CancellationToken ct = default)
    {
        // SubjectIds.Contains → `userId = ANY("SubjectIds")` on Postgres; the
        // (Enabled) index narrows the scan to the enabled grants, a small
        // set in practice.
        var rows = await db.PermissionGrants
            .AsNoTracking()
            .Where(g => g.IsActive
                        && g.Enabled
                        && g.SubjectIds.Contains(userId))
            .Select(g => new { g.PermissionGrantId, g.Name, g.Capabilities, g.Conditions })
            .ToListAsync(ct);

        return rows
            .Select(r => new PermissionGrantRow(r.PermissionGrantId, r.Name, r.Capabilities, r.Conditions))
            .ToList();
    }
}
