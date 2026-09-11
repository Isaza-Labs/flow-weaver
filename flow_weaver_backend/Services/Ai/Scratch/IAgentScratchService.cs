using System.Text.Json;

namespace flow_weaver_backend.Services.Ai.Scratch;

// S16 — façade over the AgentScratch table so tool handlers don't have
// to repeat the upsert dance. Read returns null when no row exists so
// callers can chain `?.` cleanly.
public interface IAgentScratchService
{
    Task<JsonElement?> ReadAsync(Guid conversationId, string key, CancellationToken ct);

    Task UpsertAsync(
        Guid conversationId,
        string key,
        JsonElement value,
        Guid? workflowPlanId = null,
        string? note = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ScratchEntry>> ListAsync(Guid conversationId, CancellationToken ct);
}

public sealed record ScratchEntry(string Key, JsonElement Value, string? Note, DateTime UpdatedAt);
