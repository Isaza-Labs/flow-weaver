using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class AIConversationRepository : RepositoryBase<AIConversation>, IAIConversationRepository
{
    public AIConversationRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<AIConversation?> FindForHistoryAsync(
        Guid conversationId, CancellationToken ct = default)
        => await Query(activeOnly: false, tracking: false)
            .FirstOrDefaultAsync(c => c.AIConversationId == conversationId, ct);

    public async Task<AIConversation?> FindTrackedAsync(
        Guid conversationId, CancellationToken ct = default)
        => await Query(activeOnly: false, tracking: true)
            .FirstOrDefaultAsync(c => c.AIConversationId == conversationId, ct);

    public async Task<AIConversation?> FindByExternalThreadAsync(
        Guid messagingChannelId, string externalThreadId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(c => c.MessagingChannelId == messagingChannelId
                        && c.ExternalThreadId == externalThreadId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
}
