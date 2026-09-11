using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class EmailChannelRepository : RepositoryBase<EmailChannel>, IEmailChannelRepository
{
    public EmailChannelRepository(AppDbContext db) : base(db)
    {
    }

    // Ordered by name so a deployment that ends up with two default rows (only
    // reachable by a direct DB write) still resolves deterministically.
    public async Task<EmailChannel?> FindDefaultAsync(CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(c => c.IsDefault && c.Enabled)
            .OrderBy(c => c.Name)
            .FirstOrDefaultAsync(ct);

    public async Task<EmailChannel?> FindEnabledByIdAsync(Guid channelId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(c => c.EmailChannelId == channelId && c.Enabled, ct);

    public async Task<EmailChannel?> FindEnabledByNameAsync(string name, CancellationToken ct = default)
    {
        var needle = name.Trim().ToLowerInvariant();
        return await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(c => c.Enabled && c.Name.ToLower() == needle, ct);
    }

    public async Task<IReadOnlyList<EmailChannel>> ListDefaultsTrackedAsync(CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: true)
            .Where(c => c.IsDefault)
            .ToListAsync(ct);

    public async Task<bool> NameExistsAsync(
        string name, Guid? exceptId = null, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .AnyAsync(c => c.Name.ToLower() == name.ToLower()
                && (exceptId == null || c.EmailChannelId != exceptId), ct);

    public async Task MarkSendOutcomeAsync(Guid channelId, bool ok, CancellationToken ct = default)
    {
        // Tracked load + save (not ExecuteUpdate) so the EF InMemory test
        // provider supports it too.
        var row = await Query(activeOnly: true, tracking: true)
            .FirstOrDefaultAsync(c => c.EmailChannelId == channelId, ct);
        if (row is null) return;
        row.LastSendAt = DateTime.UtcNow;
        row.LastSendStatus = ok ? "ok" : "failed";
        row.UpdatedAt = DateTime.UtcNow;
        await SaveChangesAsync(ct);
    }
}
