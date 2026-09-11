using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// AI provider reads. Generic CRUD resolves via the open-generic
// IRepository<AIProvider>; this adds the default-provider resolution and the
// by-id lookup the agent translator / LLM factory use (the latter ignores the
// active filter because the id was already resolved upstream).
public interface IAiProviderRepository : IRepository<AIProvider>
{
    // Id of the default provider — the oldest active+enabled one
    // (CreatedAt ASC) — or null when none is configured.
    Task<Guid?> FindDefaultProviderIdAsync(CancellationToken ct = default);

    // Provider by id alone (no active filter). Read-only. Callers pass an
    // already-validated id; this just rehydrates the row.
    Task<AIProvider?> GetByProviderIdAsync(Guid providerId, CancellationToken ct = default);

    // Active provider resolved by id (isGuid) or exact Name.
    // Backs the ${secret:ai_provider:...} resolver.
    Task<AIProvider?> FindActiveByIdOrNameAsync(
        Guid id, string name, bool isGuid, CancellationToken ct = default);

    // First active+enabled provider ordered by AIProviderId (stable across
    // renames), projected to id + default model. Backs the prompt analyzer.
    Task<AiProviderModelRef?> FindFirstEnabledOrderedByIdAsync(
        CancellationToken ct = default);
}

// Provider id + default model — the prompt analyzer's stable provider pick.
public sealed record AiProviderModelRef(Guid AIProviderId, string DefaultModel);
