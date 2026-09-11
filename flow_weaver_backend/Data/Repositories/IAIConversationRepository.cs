using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Conversation persistence for the agent runner. The runner loads a thread's
// prior messages (read-only) to replay history, and loads it tracked to append
// the latest turn. Both ignore the IsActive filter on purpose: a turn can still
// be persisted onto a conversation the UI has soft-deleted, matching the
// pre-repository behavior that lived in AiChatController.
public interface IAIConversationRepository : IRepository<AIConversation>
{
    // Read-only load by id (history replay).
    Task<AIConversation?> FindForHistoryAsync(
        Guid conversationId, CancellationToken ct = default);

    // Tracked load by id so the caller can append messages and
    // SaveChangesAsync.
    Task<AIConversation?> FindTrackedAsync(
        Guid conversationId, CancellationToken ct = default);

    // Resolve the active conversation for an inbound external thread (messaging
    // channels), or null on first contact. Read-only.
    Task<AIConversation?> FindByExternalThreadAsync(
        Guid messagingChannelId, string externalThreadId, CancellationToken ct = default);
}
