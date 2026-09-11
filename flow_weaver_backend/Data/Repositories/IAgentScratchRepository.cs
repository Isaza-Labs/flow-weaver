using System.Text.Json;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Per-conversation scratchpad persistence. Encapsulates the unique-index
// upsert race (insert-or-update on (ConversationId, Key)) so the
// service never touches EF Core's change tracker or DbUpdateException.
public interface IAgentScratchRepository : IRepository<AgentScratch>
{
    // Value of a single active scratch by (conversation, key), or null.
    Task<JsonElement?> ReadValueAsync(
        Guid conversationId, string key, CancellationToken ct = default);

    // Active scratches for a conversation, ordered by Key. Read-only.
    Task<IReadOnlyList<AgentScratch>> ListActiveAsync(
        Guid conversationId, CancellationToken ct = default);

    // Insert-or-update the (conversation, key) scratch, resolving a concurrent
    // insert race on the unique index. Returns Raced=true together with the
    // caught Error when a race was resolved (so the caller can log it with the
    // exception); (false, null) on the clean path.
    Task<(bool Raced, Exception? Error)> UpsertAsync(
        Guid conversationId, string key, JsonElement value,
        Guid? workflowPlanId, string? note, DateTime now, CancellationToken ct = default);
}
