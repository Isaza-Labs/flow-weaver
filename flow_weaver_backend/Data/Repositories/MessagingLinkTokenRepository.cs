using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class MessagingLinkTokenRepository
    : RepositoryBase<MessagingLinkToken>, IMessagingLinkTokenRepository
{
    public MessagingLinkTokenRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<MessagingLinkToken?> FindActiveByHashAsync(
        byte[] tokenHash, DateTime nowUtc, CancellationToken ct = default)
        => await Set
            .FirstOrDefaultAsync(
                t => t.TokenHash == tokenHash && t.ConsumedAt == null && t.ExpiresAt > nowUtc, ct);

    // virtual so a test double can substitute the set-based UPDATE, which the
    // EF InMemory provider does not implement.
    public virtual async Task<bool> TryConsumeAsync(
        Guid tokenId, Guid consumedByUserId, DateTime nowUtc, CancellationToken ct = default)
    {
        var affected = await Set
            .Where(t => t.MessagingLinkTokenId == tokenId && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.ConsumedAt, nowUtc)
                .SetProperty(t => t.ConsumedByUserId, consumedByUserId)
                .SetProperty(t => t.UpdatedAt, nowUtc), ct);
        return affected == 1;
    }

    public Task<int> DeleteExpiredOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
        => Set.Where(t => t.ExpiresAt < cutoffUtc).ExecuteDeleteAsync(ct);
}
