namespace flow_weaver_backend.Data.Repositories;

// A completed run's status + timing, for latency/error-rate aggregation.
public sealed record SloRunSample(string Status, DateTime StartedAt, DateTime CompletedAt);

// A promoted workflow's source pointer + creation time, for promotion-latency.
public sealed record SloPromotionSample(Guid? PromotedFrom, DateTime CreatedAt);

// Read-only data access for SloComputeService. Spans WorkflowRuns + Jobs +
// Workflows; the service keeps the percentile/ratio math, this only runs the
// windowed SQL.
public interface ISloRepository
{
    // Runs created since `from` that have both started and completed, projected
    // to the fields the SLO math needs.
    Task<IReadOnlyList<SloRunSample>> GetCompletedRunsAsync(
        DateTime from, CancellationToken ct = default);

    // Count of completed jobs created since `from`.
    Task<int> CountCompletedJobsAsync(DateTime from, CancellationToken ct = default);

    // Promoted (qa/production) workflows created since `from`, with their source
    // pointer + creation time.
    Task<IReadOnlyList<SloPromotionSample>> GetPromotionsAsync(
        DateTime from, CancellationToken ct = default);

    // CreatedAt of the given workflow ids, keyed by id — the promotion source
    // timestamps to diff against.
    Task<Dictionary<Guid, DateTime>> GetWorkflowCreatedAtAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}
