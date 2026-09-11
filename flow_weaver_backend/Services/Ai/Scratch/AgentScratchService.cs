using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Scratch;

// Persists per-conversation scratchpad rows. The pattern: a tool that
// discovers reusable context (device list, candidate snippets, open
// questions) calls UpsertAsync; the chat controller hydrates active
// scratches into the system prompt at the start of each turn.
//
// The unique-index upsert race (ConversationId, Key) is resolved inside
// IAgentScratchRepository — even if two concurrent handlers collide, the
// repository falls back to UPDATE on the winning row.
public sealed class AgentScratchService : IAgentScratchService
{
    private readonly IAgentScratchRepository _scratch;
    private readonly ICurrentUser _caller;
    private readonly ILogger<AgentScratchService> _logger;

    public AgentScratchService(
        IAgentScratchRepository scratch,
        ICurrentUser caller,
        ILogger<AgentScratchService> logger)
    {
        _scratch = scratch;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement?> ReadAsync(Guid conversationId, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return await _scratch.ReadValueAsync(conversationId, key, ct);
    }

    public async Task UpsertAsync(
        Guid conversationId,
        string key,
        JsonElement value,
        Guid? workflowPlanId = null,
        string? note = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        var now = DateTime.UtcNow;
        var (raced, error) = await _scratch.UpsertAsync(conversationId, key, value, workflowPlanId, note, now, ct);

        if (raced)
            _logger.LogWarning(
                error,
                "ai.scratch.upsert.retry conversation_id={ConversationId} key={Key}",
                conversationId, key);
    }

    public async Task<IReadOnlyList<ScratchEntry>> ListAsync(Guid conversationId, CancellationToken ct)
    {
        var rows = await _scratch.ListActiveAsync(conversationId, ct);
        return rows.Select(s => new ScratchEntry(s.Key, s.Value, s.Note, s.UpdatedAt)).ToList();
    }
}
