using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class MessagingChannelRepository : RepositoryBase<MessagingChannel>, IMessagingChannelRepository
{
    public MessagingChannelRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<MessagingChannel?> FindActiveByIdAsync(Guid channelId, CancellationToken ct = default)
        => await Set.AsNoTracking()
            .FirstOrDefaultAsync(c => c.MessagingChannelId == channelId && c.IsActive, ct);

    public async Task<MessagingChannel?> FindTrackedByIdAsync(Guid channelId, CancellationToken ct = default)
        => await Set.FirstOrDefaultAsync(c => c.MessagingChannelId == channelId, ct);

    public async Task<IReadOnlyList<MessagingChannel>> ListByProviderAsync(
        string provider, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(c => c.Provider == provider && c.Enabled)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MessagingChannel>> ListEnabledByProviderAllTenantsAsync(
        string provider, CancellationToken ct = default)
        => await Set.AsNoTracking()
            .Where(c => c.Provider == provider && c.Enabled && c.IsActive)
            .ToListAsync(ct);
}
