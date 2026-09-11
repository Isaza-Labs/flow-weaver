using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// IntegrationAction reads. Generic CRUD resolves via the open-generic
// IRepository<IntegrationAction>; this adds the worker-dispatch by-id lookup.
public interface IIntegrationActionRepository : IRepository<IntegrationAction>
{
    // By-id lookup, active only. The worker resolves the action from the
    // dispatched step input (already authorised upstream) — mirrors the
    // original handler query.
    Task<IntegrationAction?> FindActiveByIdAsync(Guid actionId, CancellationToken ct = default);

    // Active actions whose id is in `ids`, projected to
    // (id, name, method, path) for the simulator's existence check.
    Task<IReadOnlyList<IntegrationActionRef>> ListActiveRefsByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    // Paths of one integration's active actions. The health checker derives
    // an authenticated API root from their common prefix so the probe can
    // exercise the integration's credentials, not just reachability.
    Task<IReadOnlyList<string>> ListActivePathsByIntegrationAsync(
        Guid integrationId, CancellationToken ct = default);

    // Active actions of one integration. A workflow bundle identifies an action
    // portably as (integration slug, action name), so the importer resolves it
    // by listing the target integration's actions and matching on name.
    Task<IReadOnlyList<IntegrationAction>> ListActiveByIntegrationAsync(
        Guid integrationId, CancellationToken ct = default);
}

// (id, name, method, path) projection for the workflow simulator.
public sealed record IntegrationActionRef(Guid IntegrationActionId, string Name, string Method, string Path);
