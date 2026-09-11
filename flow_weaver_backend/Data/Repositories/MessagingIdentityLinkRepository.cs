using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class MessagingIdentityLinkRepository
    : RepositoryBase<MessagingIdentityLink>, IMessagingIdentityLinkRepository
{
    public MessagingIdentityLinkRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<MessagingIdentityLink?> FindByExternalAsync(
        Guid channelId, string externalWorkspaceId, string externalUserId,
        CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(
                l => l.MessagingChannelId == channelId
                     && l.ExternalWorkspaceId == externalWorkspaceId
                     && l.ExternalUserId == externalUserId, ct);

    public async Task<IReadOnlyList<MessagingIdentityLink>> ListByChannelAsync(
        Guid channelId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(l => l.MessagingChannelId == channelId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);

    public async Task<MessagingIdentityLink?> FindTrackedByIdAsync(
        Guid linkId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: true)
            .FirstOrDefaultAsync(l => l.MessagingIdentityLinkId == linkId, ct);
}
