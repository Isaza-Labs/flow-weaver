using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class MessagingDeliveryRepository
    : RepositoryBase<MessagingDelivery>, IMessagingDeliveryRepository
{
    public MessagingDeliveryRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<bool> InboundExistsAsync(
        Guid channelId, string providerEventId, CancellationToken ct = default)
        => await Db.Set<MessagingInboundEvent>().AsNoTracking()
            .AnyAsync(e => e.MessagingChannelId == channelId
                           && e.ProviderEventId == providerEventId, ct);

    public void AddInbound(MessagingInboundEvent inbound) =>
        Db.Set<MessagingInboundEvent>().Add(inbound);

    public void AddDelivery(MessagingDelivery delivery) =>
        Db.Set<MessagingDelivery>().Add(delivery);

    public async Task<IReadOnlyList<MessagingInboundEvent>> ListInboundAsync(
        Guid channelId, int limit, CancellationToken ct = default)
        => await Db.Set<MessagingInboundEvent>().AsNoTracking()
            .Where(e => e.MessagingChannelId == channelId)
            .OrderByDescending(e => e.At)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MessagingDelivery>> ListDeliveriesAsync(
        Guid channelId, int limit, CancellationToken ct = default)
        => await Query(activeOnly: false, tracking: false)
            .Where(d => d.MessagingChannelId == channelId)
            .OrderByDescending(d => d.At)
            .Take(limit)
            .ToListAsync(ct);

    public virtual async Task<bool> TryClaimInboundForProcessingAsync(
        Guid inboundEventId, DateTime nowUtc, CancellationToken ct = default)
    {
        var affected = await Db.Set<MessagingInboundEvent>()
            .Where(e => e.MessagingInboundEventId == inboundEventId
                        && e.Status == MessagingInboundEvent.StatusQueued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, MessagingInboundEvent.StatusProcessing)
                .SetProperty(e => e.UpdatedAt, nowUtc), ct);
        return affected == 1;
    }

    public virtual async Task MarkInboundStatusAsync(
        Guid inboundEventId, string status, DateTime nowUtc, CancellationToken ct = default)
    {
        await Db.Set<MessagingInboundEvent>()
            .Where(e => e.MessagingInboundEventId == inboundEventId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, status)
                .SetProperty(e => e.UpdatedAt, nowUtc), ct);
    }

    public Task<int> DeleteInboundOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
        => Db.Set<MessagingInboundEvent>().Where(e => e.At < cutoffUtc).ExecuteDeleteAsync(ct);

    public Task<int> DeleteDeliveriesOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
        => Set.Where(d => d.At < cutoffUtc).ExecuteDeleteAsync(ct);
}
