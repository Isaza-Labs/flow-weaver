using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class IntegrationRepository : RepositoryBase<Integration>, IIntegrationRepository
{
    public IntegrationRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<HashSet<string>> ListTakenSlugsAsync(CancellationToken ct = default)
        // activeOnly:false — the unique index spans soft-deleted rows too.
        => (await Query(activeOnly: false, tracking: false)
                .Where(i => i.Slug != null)
                .Select(i => i.Slug!)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

    public async Task<Integration?> FindActiveBySlugAsync(string slug, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(i => i.Slug == slug, ct);

    public async Task<IReadOnlyList<IntegrationSummary>> ListActiveSummariesAsync(
        CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Select(i => new IntegrationSummary(i.IntegrationId, i.Name, i.Type))
            .ToListAsync(ct);

    public async Task<Integration?> FindActiveByIdAsync(Guid integrationId, CancellationToken ct = default)
        => await Set.AsNoTracking()
            .FirstOrDefaultAsync(i => i.IntegrationId == integrationId && i.IsActive, ct);

    public async Task<Integration?> FindActiveByNameAsync(string name, CancellationToken ct = default)
    {
        var needle = name.Trim().ToLowerInvariant();
        return await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(i => i.Name.ToLower() == needle, ct);
    }

    public async Task<Integration?> FindActiveByIdOrNameAsync(
        Guid id, string name, bool isGuid, CancellationToken ct = default)
    {
        var q = Query(activeOnly: true, tracking: false);
        return isGuid
            ? await q.FirstOrDefaultAsync(i => i.IntegrationId == id, ct)
            : await q.FirstOrDefaultAsync(i => i.Name == name, ct);
    }

    public async Task<IReadOnlyList<IntegrationRef>> ListActiveRefsByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Array.Empty<IntegrationRef>();
        return await Query(activeOnly: true, tracking: false)
            .Where(i => ids.Contains(i.IntegrationId))
            .Select(i => new IntegrationRef(i.IntegrationId, i.Name, i.Status))
            .ToListAsync(ct);
    }
}
