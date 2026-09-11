using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// AiPromptSkill persistence. Adds the by-name batch lookup the integration
// bundle needs to reactivate tombstoned rows (the unique index spans
// active + inactive, so the upsert must see soft-deleted rows too).
public interface IAiPromptSkillRepository : IRepository<AiPromptSkill>
{
    // Tracked rows (active OR inactive) for the given names, keyed by Name.
    // Tracked so the caller can flip IsActive / mutate in place.
    Task<Dictionary<string, AiPromptSkill>> GetByNamesAsync(
        IReadOnlyCollection<string> names, CancellationToken ct = default);

    // Active rows ordered SortOrder → Name (catalog display order, not CreatedAt).
    Task<IReadOnlyList<AiPromptSkill>> ListOrderedAsync(
        int limit, int offset, CancellationToken ct = default);

    // Single row by exact name, active OR inactive, tracked — backs the upsert
    // path that reactivates a tombstoned skill instead of colliding with the
    // (Name) unique index.
    Task<AiPromptSkill?> FindByNameAsync(string name, CancellationToken ct = default);

    // True when another row (active OR inactive) already uses this name — the
    // rename clash check.
    Task<bool> NameExistsForOtherAsync(
        string name, Guid excludeId, CancellationToken ct = default);

    // MAX(UpdatedAt) over the active skills — the cache freshness probe
    // the prompt loader compares against its cached snapshot. Null when none.
    Task<DateTime?> MaxActiveUpdatedAtAsync(CancellationToken ct = default);

    // Content of every active skill, ordered SortOrder → Name (base.md leads).
    // Read-only.
    Task<IReadOnlyList<string>> ListActiveContentsOrderedAsync(
        CancellationToken ct = default);

    // Same, restricted to rows with no IntegrationId. Rows tied to an integration
    // are loaded on demand by ScopedSkillCatalog, not concatenated into every turn.
    Task<IReadOnlyList<string>> ListActiveGlobalContentsOrderedAsync(
        CancellationToken ct = default);
}
