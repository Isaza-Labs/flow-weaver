using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Integration reads. Generic CRUD resolves via the open-generic
// IRepository<Integration>; this adds the lightweight catalog projection the
// import dependency resolver needs (id/name/type only, no secrets or config).
public interface IIntegrationRepository : IRepository<Integration>
{
    // Every active integration, projected to the fields the
    // import matcher uses. Read-only.
    Task<IReadOnlyList<IntegrationSummary>> ListActiveSummariesAsync(
        CancellationToken ct = default);

    // Every slug in use, including soft-deleted rows: the unique index does not
    // care about IsActive, so reusing a deleted row's slug would throw on save.
    Task<HashSet<string>> ListTakenSlugsAsync(CancellationToken ct = default);

    // Active integration with this exact slug — the cross-instance identity an
    // imported workflow bundle resolves against. Null when this instance has
    // no such integration, which the importer reports rather than papering over.
    Task<Integration?> FindActiveBySlugAsync(string slug, CancellationToken ct = default);

    // By-id lookup, active only. The worker dispatch path resolves an
    // integration straight from the step input (already authorised
    // upstream) — mirrors the original handler query.
    Task<Integration?> FindActiveByIdAsync(Guid integrationId, CancellationToken ct = default);

    // First active integration whose Name matches case-insensitively.
    // Names are not unique, so the first match wins.
    Task<Integration?> FindActiveByNameAsync(string name, CancellationToken ct = default);

    // Active integration resolved by id (isGuid) or EXACT Name.
    // Backs the ${secret:integration:...} resolver (distinct from the case-
    // insensitive FindActiveByNameAsync above).
    Task<Integration?> FindActiveByIdOrNameAsync(
        Guid id, string name, bool isGuid, CancellationToken ct = default);

    // Active integrations whose id is in `ids`, projected to
    // (id, name, status) for the simulator's existence + health check.
    Task<IReadOnlyList<IntegrationRef>> ListActiveRefsByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}

// Minimal integration shape for name/type matching during import analysis.
public sealed record IntegrationSummary(Guid IntegrationId, string Name, string Type);

// (id, name, status) projection for the workflow simulator.
public sealed record IntegrationRef(Guid IntegrationId, string Name, string Status);
