using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class IntegrationActionRepository : RepositoryBase<IntegrationAction>, IIntegrationActionRepository
{
    public IntegrationActionRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<IntegrationAction>> ListActiveByIntegrationAsync(
        Guid integrationId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(a => a.IntegrationId == integrationId)
            .ToListAsync(ct);

    public async Task<IntegrationAction?> FindActiveByIdAsync(Guid actionId, CancellationToken ct = default)
        => await Set.AsNoTracking()
            .FirstOrDefaultAsync(a => a.IntegrationActionId == actionId && a.IsActive, ct);

    public async Task<IReadOnlyList<IntegrationActionRef>> ListActiveRefsByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Array.Empty<IntegrationActionRef>();
        return await Query(activeOnly: true, tracking: false)
            .Where(a => ids.Contains(a.IntegrationActionId))
            .Select(a => new IntegrationActionRef(a.IntegrationActionId, a.Name, a.Method, a.Path))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListActivePathsByIntegrationAsync(
        Guid integrationId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(a => a.IntegrationId == integrationId)
            .Select(a => a.Path)
            .ToListAsync(ct);
}
