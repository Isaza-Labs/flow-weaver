using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

// Admin dashboard aggregates. Returns pre-bucketed time series so the
// frontend can render charts without doing per-event math. Both endpoints
// are Admin-only and cached by the ReadHeavy rate-limit bucket since they
// scan a whole window at once.
[ApiController]
[Route("api/admin/metrics")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class AdminMetricsController : ControllerBase
{
    // Hard cap on window size. 90 days keeps the aggregation bounded even
    // if run volume is high. Frontend currently asks for 7.
    private const int MaxDays = 90;
    private const int DefaultDays = 7;

    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;

    public AdminMetricsController(AppDbContext db, ICurrentUser caller)
    {
        _db = db;
        _caller = caller;
    }

    // Runs bucketed by UTC date and status. Always returns one row per day
    // in the window (including zero-run days) so charts have a stable
    // x-axis without the frontend having to backfill missing buckets.
    [HttpGet("runs")]
    public async Task<ActionResult<RunMetricsResponse>> Runs(
        [FromQuery] int days = DefaultDays,
        CancellationToken ct = default)
    {
        var window = Math.Clamp(days, 1, MaxDays);
        var from = DateTime.UtcNow.Date.AddDays(-(window - 1));

        var rows = await _db.WorkflowRuns.AsNoTracking()
            .Where(r => r.IsActive
                        && r.CreatedAt >= from)
            .GroupBy(r => new { Date = r.CreatedAt.Date, r.Status })
            .Select(g => new { g.Key.Date, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var series = BuildDailyBuckets(from, window, rows.Select(r => (r.Date, r.Status, r.Count)));

        // Top N workflows by failed-run count in the window. Drives the
        // "Top failing workflows" table next to the chart.
        var topFailing = await _db.WorkflowRuns.AsNoTracking()
            .Where(r => r.IsActive
                        && r.CreatedAt >= from
                        && (r.Status == "failed" || r.Status == "failure"))
            .GroupBy(r => r.WorkflowId)
            .Select(g => new { WorkflowId = g.Key, Failed = g.Count() })
            .OrderByDescending(x => x.Failed)
            .Take(5)
            .ToListAsync(ct);

        var names = await _db.Workflows.AsNoTracking()
            .Where(w => topFailing.Select(tf => tf.WorkflowId).Contains(w.WorkflowId))
            .Select(w => new { w.WorkflowId, w.Name })
            .ToListAsync(ct);
        var nameLookup = names.ToDictionary(n => n.WorkflowId, n => n.Name);

        return Ok(new RunMetricsResponse
        {
            Days = window,
            From = from,
            Series = series,
            TopFailing = topFailing.Select(tf => new TopFailingWorkflow
            {
                WorkflowId = tf.WorkflowId,
                WorkflowName = nameLookup.GetValueOrDefault(tf.WorkflowId, "(deleted)"),
                FailedCount = tf.Failed,
            }).ToList(),
        });
    }

    // SLO snapshot. Delegates the heavy lifting to SloComputeService
    // so the daily breach watcher (SloBreachWatcherService) consumes the
    // exact same computation the dashboard does.
    [HttpGet("slo")]
    public async Task<ActionResult<SloResponse>> Slo(
        [FromQuery] int days = DefaultDays,
        [FromServices] flow_weaver_backend.Services.Slo.SloComputeService compute = null!,
        CancellationToken ct = default)
    {
        var snapshot = await compute.ComputeAsync(days, ct);
        return Ok(new SloResponse
        {
            Days = snapshot.Days,
            From = snapshot.From,
            Slos = snapshot.Slos.Select(e => new Slo
            {
                Key = e.Key,
                Label = e.Label,
                Unit = e.Unit,
                Target = e.Target,
                Value = e.Value,
                Better = e.Better,
            }).ToList(),
        });
    }

    // Auth events bucketed by UTC date and event kind. Useful for spotting
    // spikes in failed logins or lockouts alongside normal traffic.
    [HttpGet("auth")]
    public async Task<ActionResult<AuthMetricsResponse>> Auth(
        [FromQuery] int days = DefaultDays,
        CancellationToken ct = default)
    {
        var window = Math.Clamp(days, 1, MaxDays);
        var from = DateTime.UtcNow.Date.AddDays(-(window - 1));


        var rows = await _db.AuthEvents.AsNoTracking()
            .Where(e => e.At >= from)
            .GroupBy(e => new { Date = e.At.Date, e.Event })
            .Select(g => new { g.Key.Date, g.Key.Event, Count = g.Count() })
            .ToListAsync(ct);

        var series = BuildDailyBuckets(from, window, rows.Select(r =>
            (r.Date, r.Event, r.Count)));

        return Ok(new AuthMetricsResponse
        {
            Days = window,
            From = from,
            Series = series,
        });
    }

    // Device-inventory usage report. The
    // commercial definition of a "managed device" is a signed product decision
    // that does NOT live in code — so this endpoint stays deliberately neutral:
    // it exposes the raw breakdown by every dimension that could matter plus the
    // count for each PLAUSIBLE definition, each with its exact WHERE clause. That
    // makes the report auditable — every number here is reproducible by running
    // its documented rule as a direct `SELECT COUNT(*) FROM devices …`. When
    // product signs the definition, "managed_devices" is simply whichever
    // candidate they pick. Admin-only.
    [HttpGet("devices")]
    public async Task<ActionResult<DeviceUsageResponse>> Devices(CancellationToken ct = default)
    {
        var q = _db.Devices.AsNoTracking();
        // A never-synced device (discovered by an inventory source but never
        // touched) has LastSyncAt at its default (0001-01-01).
        var neverEpoch = default(DateTime);

        var total = await q.CountAsync(ct);
        var active = await q.CountAsync(d => d.IsActive, ct);
        var activeQaLab = await q.CountAsync(d => d.IsActive && d.AllowQa, ct);
        var activeNonQa = await q.CountAsync(d => d.IsActive && !d.AllowQa, ct);
        var activeFromInventory = await q.CountAsync(d => d.IsActive && d.SourceId != null, ct);
        var activeManual = await q.CountAsync(d => d.IsActive && d.SourceId == null, ct);
        var activeNeverSynced = await q.CountAsync(d => d.IsActive && d.LastSyncAt == neverEpoch, ct);
        var activeSynced = await q.CountAsync(d => d.IsActive && d.LastSyncAt != neverEpoch, ct);

        // Candidate "managed device" definitions.
        var candB = activeNonQa;
        var candC = await q.CountAsync(d => d.IsActive && !d.AllowQa && d.LastSyncAt != neverEpoch, ct);
        var candD = await q.CountAsync(d => d.IsActive && !d.AllowQa && d.Status == "reachable", ct);

        var byStatus = (await q.Where(d => d.IsActive)
                .GroupBy(d => d.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => string.IsNullOrEmpty(x.Status) ? "(unset)" : x.Status, x => x.Count);

        return Ok(new DeviceUsageResponse
        {
            GeneratedAt = DateTime.UtcNow,
            Total = total,
            Breakdown = new Dictionary<string, int>
            {
                ["active"] = active,
                ["inactive"] = total - active,
                ["active_qa_lab"] = activeQaLab,
                ["active_non_qa_lab"] = activeNonQa,
                ["active_from_inventory"] = activeFromInventory,
                ["active_manual"] = activeManual,
                ["active_never_synced"] = activeNeverSynced,
                ["active_synced"] = activeSynced,
            },
            ByStatusActive = byStatus,
            CandidateManagedCounts = new List<ManagedDeviceCandidate>
            {
                new() { Key = "A_all_active", Count = active, Rule = "IsActive" },
                new() { Key = "B_active_excluding_qa_lab", Count = candB, Rule = "IsActive AND NOT AllowQa" },
                new() { Key = "C_active_excluding_qa_and_never_synced", Count = candC, Rule = "IsActive AND NOT AllowQa AND LastSyncAt <> '0001-01-01'" },
                new() { Key = "D_active_excluding_qa_reachable", Count = candD, Rule = "IsActive AND NOT AllowQa AND Status = 'reachable'" },
            },
            Note = "Candidate definitions of a managed device. Each candidate is reproducible with a direct "
                + "SELECT COUNT(*) FROM devices WHERE <rule>.",
        });
    }

    // Flattens a (date, key, count) enumeration into one row per day in the
    // window. Missing days become a bucket with an empty Counts dict so the
    // chart can render a zero bar rather than skipping that day entirely.
    private static List<DailyBucket> BuildDailyBuckets(
        DateTime from, int days, IEnumerable<(DateTime Date, string Key, int Count)> rows)
    {
        var byDate = rows
            .GroupBy(r => r.Date)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Sum(y => y.Count)));

        var buckets = new List<DailyBucket>(days);
        for (var i = 0; i < days; i++)
        {
            var d = from.AddDays(i);
            buckets.Add(new DailyBucket
            {
                Date = d,
                Counts = byDate.TryGetValue(d, out var c) ? c : new Dictionary<string, int>(),
            });
        }
        return buckets;
    }
}

public class DailyBucket
{
    [System.Text.Json.Serialization.JsonPropertyName("date")]
    public DateTime Date { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("counts")]
    public Dictionary<string, int> Counts { get; set; } = new();
}

public class RunMetricsResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("days")]
    public int Days { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("from")]
    public DateTime From { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("series")]
    public List<DailyBucket> Series { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("top_failing")]
    public List<TopFailingWorkflow> TopFailing { get; set; } = new();
}

public class TopFailingWorkflow
{
    [System.Text.Json.Serialization.JsonPropertyName("workflow_id")]
    public Guid WorkflowId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("workflow_name")]
    public string WorkflowName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("failed_count")]
    public int FailedCount { get; set; }
}

public class AuthMetricsResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("days")]
    public int Days { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("from")]
    public DateTime From { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("series")]
    public List<DailyBucket> Series { get; set; } = new();
}

public class DeviceUsageResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("generated_at")]
    public DateTime GeneratedAt { get; set; }

    // Every device row (active + soft-deleted).
    [System.Text.Json.Serialization.JsonPropertyName("total")]
    public int Total { get; set; }

    // Raw counts per dimension — each is a direct COUNT with a WHERE clause.
    [System.Text.Json.Serialization.JsonPropertyName("breakdown")]
    public Dictionary<string, int> Breakdown { get; set; } = new();

    // Active devices grouped by Status.
    [System.Text.Json.Serialization.JsonPropertyName("by_status_active")]
    public Dictionary<string, int> ByStatusActive { get; set; } = new();

    // The count under each plausible "managed device" definition, for product
    // to pick from and sign. Not authoritative until then.
    [System.Text.Json.Serialization.JsonPropertyName("candidate_managed_counts")]
    public List<ManagedDeviceCandidate> CandidateManagedCounts { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;
}

public class ManagedDeviceCandidate
{
    [System.Text.Json.Serialization.JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("count")]
    public int Count { get; set; }

    // The exact rule (WHERE clause) this count applies — so the report can be
    // verified against a direct DB query.
    [System.Text.Json.Serialization.JsonPropertyName("rule")]
    public string Rule { get; set; } = string.Empty;
}

public class SloResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("days")]
    public int Days { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("from")]
    public DateTime From { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("slos")]
    public List<Slo> Slos { get; set; } = new();
}

public class Slo
{
    [System.Text.Json.Serialization.JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("target")]
    public double Target { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("value")]
    public double? Value { get; set; }

    // "lower" → smaller is better (latency, error rate); "higher" →
    // bigger is better (throughput). Frontend uses this to flip the
    // comparison direction without hard-coding per-SLO logic.
    [System.Text.Json.Serialization.JsonPropertyName("better")]
    public string Better { get; set; } = "lower";
}
