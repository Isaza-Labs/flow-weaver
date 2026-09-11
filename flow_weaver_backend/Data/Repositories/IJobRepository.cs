using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Job reads. Adds the queue-stats aggregation (GROUP BY status) the dashboard
// needs, which the generic CRUD can't express.
public interface IJobRepository : IRepository<Job>
{
    // Active job counts grouped by status. Keys are whatever
    // statuses exist in the data; the caller backfills the canonical zero rows.
    Task<Dictionary<string, int>> CountByStatusAsync(CancellationToken ct = default);

    // Count of jobs whose status is one of `statuses`. Does NOT
    // apply the soft-delete filter — it mirrors the webhook backpressure gate,
    // which counts every pending/claimed job regardless of IsActive.
    Task<int> CountByStatusesAsync(
        IReadOnlyCollection<string> statuses, CancellationToken ct = default);
}
