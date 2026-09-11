using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// AiApiSpec persistence. Adds the by-api batch lookup the integration bundle
// needs to reactivate tombstoned rows (the unique index spans active +
// inactive, so the upsert must see soft-deleted rows too).
public interface IAiApiSpecRepository : IRepository<AiApiSpec>
{
    // Tracked rows (active OR inactive) for the given api identifiers, keyed by
    // Api. Tracked so the caller can flip IsActive / mutate in place.
    Task<Dictionary<string, AiApiSpec>> GetByApisAsync(
        IReadOnlyCollection<string> apis, CancellationToken ct = default);

    // Active rows ordered by Api (catalog display order, not CreatedAt).
    Task<IReadOnlyList<AiApiSpec>> ListOrderedAsync(
        int limit, int offset, CancellationToken ct = default);

    // Single row by exact api id, active OR inactive, tracked — backs the upsert
    // path that reactivates a tombstoned spec.
    Task<AiApiSpec?> FindByApiAsync(string api, CancellationToken ct = default);

    // True when another row (active OR inactive) already uses this api id — the
    // rename clash check.
    Task<bool> ApiExistsForOtherAsync(
        string api, Guid excludeId, CancellationToken ct = default);

    // (Api, Content) for every active spec — the source the
    // in-memory operation index parses on reload. Read-only.
    Task<IReadOnlyList<ApiSpecContent>> ListActiveApiContentsAsync(
        CancellationToken ct = default);

    // Single active spec by exact api id (read-only) — backs the REST executor's
    // on-demand reparse.
    Task<AiApiSpec?> FindActiveByApiAsync(string api, CancellationToken ct = default);

    // Raw spec Content for a single active api id (read-only), or null. Backs
    // operation_detail's on-demand yaml reparse.
    Task<string?> GetActiveContentByApiAsync(string api, CancellationToken ct = default);

    // (Api → Content) for the active specs matching the given api identifiers,
    // read-only. Backs discover_operations(include_details=true), which loads
    // each yaml once even when several operations share the same api.
    Task<Dictionary<string, string>> GetActiveContentsByApisAsync(
        IReadOnlyCollection<string> apis, CancellationToken ct = default);
}

// (Api, Content) projection used to rebuild the operation index.
public sealed record ApiSpecContent(string Api, string Content);
