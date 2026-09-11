using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class AiProviderRepository : RepositoryBase<AIProvider>, IAiProviderRepository
{
    public AiProviderRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<Guid?> FindDefaultProviderIdAsync(CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(p => p.Enabled)
            .OrderBy(p => p.CreatedAt)
            .Select(p => (Guid?)p.AIProviderId)
            .FirstOrDefaultAsync(ct);

    public async Task<AIProvider?> GetByProviderIdAsync(Guid providerId, CancellationToken ct = default)
        => await Set.AsNoTracking()
            .FirstOrDefaultAsync(p => p.AIProviderId == providerId, ct);

    public async Task<AIProvider?> FindActiveByIdOrNameAsync(
        Guid id, string name, bool isGuid, CancellationToken ct = default)
    {
        var q = Query(activeOnly: true, tracking: false);
        return isGuid
            ? await q.FirstOrDefaultAsync(p => p.AIProviderId == id, ct)
            : await q.FirstOrDefaultAsync(p => p.Name == name, ct);
    }

    public async Task<AiProviderModelRef?> FindFirstEnabledOrderedByIdAsync(
        CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(p => p.Enabled)
            .OrderBy(p => p.AIProviderId)
            .Select(p => new AiProviderModelRef(p.AIProviderId, p.DefaultModel))
            .FirstOrDefaultAsync(ct);
}
