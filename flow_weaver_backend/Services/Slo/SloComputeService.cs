using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Engine;

namespace flow_weaver_backend.Services.Slo;

public sealed record SloEntry(
    string Key,
    string Label,
    string Unit,
    double Target,
    double? Value,
    string Better);

public sealed record SloSnapshot(int Days, DateTime From, IReadOnlyList<SloEntry> Slos);

// Centralised computation of the four platform SLOs. Used by both
// AdminMetricsController.Slo (synchronous request from the admin
// dashboard) and SloBreachWatcher (the daily background sweep).
//
// The algorithm matches AdminMetricsController's previous inline logic.
// Lifting it out lets the watcher consume the same numbers the dashboard
// shows without duplicating the SQL.
public sealed class SloComputeService
{
    private readonly ISloRepository _repo;

    public SloComputeService(ISloRepository repo)
    {
        _repo = repo;
    }

    // Targets are built-in defaults today. When configurable overrides
    // land they should be loaded and merged here.
    public static IReadOnlyList<(string Key, string Label, string Unit, double Target, string Better)> Definitions { get; } = new (string, string, string, double, string)[]
    {
        ("run_latency_p95_seconds",   "Run latency (p95)",         "s",       600,    "lower"),
        ("error_rate",                "Run error rate",            "ratio",   0.05,   "lower"),
        ("throughput_jobs_per_hour",  "Throughput",                "jobs/hr", 5,      "higher"),
        ("promotion_latency_seconds", "Promotion latency (median)","s",       86400,  "lower"),
    };

    public async Task<SloSnapshot> ComputeAsync(int days, CancellationToken ct)
    {
        var window = Math.Clamp(days, 1, 90);
        var from = DateTime.UtcNow.Date.AddDays(-(window - 1));

        var runs = await _repo.GetCompletedRunsAsync(from, ct);

        var latencies = runs
            .Select(r => (r.CompletedAt - r.StartedAt).TotalSeconds)
            .Where(s => s >= 0)
            .OrderBy(s => s)
            .ToList();
        double? p95 = latencies.Count > 0
            ? latencies[(int)Math.Min(latencies.Count - 1, Math.Ceiling(latencies.Count * 0.95) - 1)]
            : null;

        // Cancelled runs don't count toward error rate (cancel ≠ error).
        var scoredRuns = runs
            .Where(r => !string.Equals(r.Status, RunStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var total = scoredRuns.Count;
        var failed = scoredRuns.Count(r =>
            string.Equals(r.Status, RunStatus.Failed, StringComparison.OrdinalIgnoreCase));
        double? errorRate = total > 0 ? (double)failed / total : null;

        var jobs = await _repo.CountCompletedJobsAsync(from, ct);
        double? throughputPerHour = window > 0 ? (double)jobs / (window * 24.0) : null;

        var promoted = await _repo.GetPromotionsAsync(from, ct);
        var sourceIds = promoted.Where(p => p.PromotedFrom.HasValue)
            .Select(p => p.PromotedFrom!.Value).Distinct().ToList();
        var sources = await _repo.GetWorkflowCreatedAtAsync(sourceIds, ct);
        var promotionLatencies = promoted
            .Where(p => p.PromotedFrom.HasValue && sources.ContainsKey(p.PromotedFrom!.Value))
            .Select(p => (p.CreatedAt - sources[p.PromotedFrom!.Value]).TotalSeconds)
            .Where(s => s >= 0)
            .OrderBy(s => s)
            .ToList();
        double? promotionMedian = promotionLatencies.Count > 0
            ? promotionLatencies[promotionLatencies.Count / 2]
            : null;

        var values = new Dictionary<string, double?>
        {
            ["run_latency_p95_seconds"]   = p95,
            ["error_rate"]                = errorRate,
            ["throughput_jobs_per_hour"]  = throughputPerHour,
            ["promotion_latency_seconds"] = promotionMedian,
        };

        var entries = Definitions.Select(d => new SloEntry(
            d.Key, d.Label, d.Unit, d.Target,
            values.TryGetValue(d.Key, out var v) ? v : null,
            d.Better)).ToList();

        return new SloSnapshot(window, from, entries);
    }

    // True when the value violates the target. Null values are treated
    // as "no signal" — not a breach (avoids false positives when nothing
    // has run yet).
    public static bool IsBreach(SloEntry entry)
    {
        if (entry.Value is null) return false;
        return entry.Better switch
        {
            "lower" => entry.Value > entry.Target,
            "higher" => entry.Value < entry.Target,
            _ => false,
        };
    }
}
