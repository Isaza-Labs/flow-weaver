using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Scheduler;
using Microsoft.EntityFrameworkCore;
using TriggerModel = flow_weaver_backend.Models.WorkflowTrigger;

namespace flow_weaver_backend.BackgroundServices;

// Fires scheduled workflow triggers. Polls every PollInterval for triggers
// whose NextRunAt has come due, enqueues a run for each via IWorkflowExecutor,
// then advances NextRunAt to the next cron occurrence.
//
// Precision: a trigger fires within one poll tick of its scheduled time.
// NextRunAt is anchored to the cron's absolute wall-clock occurrences (not
// "last fire + interval"), so drift never accumulates — a 30s cron stays on
// :00/:30 boundaries. Set the tick below the smallest cadence you need
// (default 10s; env Scheduler__PollSeconds to tune, e.g. 5 for tighter 30s).
//
// Concurrency-safe (FR-027 / DEF-004): each due occurrence is CLAIMED with an
// atomic compare-and-swap on NextRunAt (see FireAsync) before the run is
// enqueued, so even if the API tier is scaled to multiple replicas a trigger
// fires EXACTLY once — the replica that wins the CAS enqueues, the rest bow
// out. Still registered only in the API (non-worker) process, mirroring
// SloBreachWatcher / AllowPrivateNetworkReminder; the claim is what makes
// multi-replica safe rather than relying on single-owner registration.
public sealed class SchedulerHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkflowExecutor _executor;
    private readonly ILogger<SchedulerHostedService> _logger;

    private static readonly TimeSpan PollInterval = ResolvePollInterval();
    private const int MaxPerTick = 100;

    public SchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        IWorkflowExecutor executor,
        ILogger<SchedulerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _executor = executor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "scheduler.started poll_seconds={Seconds}", PollInterval.TotalSeconds);

        // Activate schedules that predate this feature (or a NextRunAt reset):
        // any enabled schedule trigger with a null NextRunAt gets its next
        // occurrence computed once, so it starts firing without an immediate
        // surprise catch-up run on deploy.
        try { await BackfillNextRunAtAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogError(ex, "scheduler.backfill.failed"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "scheduler.tick.failed");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("scheduler.stopped");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        List<TriggerModel> due;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            due = await db.WorkflowTriggers
                .AsNoTracking()
                .Where(t => t.IsActive && t.Enabled
                    && t.NextRunAt != null && t.NextRunAt <= now
                    && (t.Type == "cron" || t.Type == "schedule"))
                .OrderBy(t => t.NextRunAt)
                .Take(MaxPerTick)
                .ToListAsync(ct);
        }

        if (due.Count == 0) return;

        _logger.LogInformation("scheduler.tick due_count={Count}", due.Count);
        foreach (var trigger in due)
        {
            ct.ThrowIfCancellationRequested();
            await FireAsync(trigger, now, ct);
        }
    }

    private async Task FireAsync(TriggerModel trigger, DateTime now, CancellationToken ct)
    {
        // Compute the next occurrence from `now`. Cron occurrences are absolute
        // wall-clock times and strictly AFTER `now` (Cronos inclusive:false), so
        // re-anchoring here never accumulates drift — and, critically for the
        // claim below, `next` can never equal the value we observed as due.
        var next = CronSchedule.ComputeNextUtc(
            trigger.CronExpression, trigger.Timezone, now, out var cronError);

        // FR-027 / DEF-004 — atomic claim. Advance NextRunAt with a
        // compare-and-swap against the EXACT value we observed as due. This one
        // UPDATE both CLAIMS the occurrence and advances the schedule: even if
        // the API tier is scaled to multiple replicas, only the replica whose
        // `NextRunAt == observed` still holds wins the row (rows affected = 1);
        // the others see 0 rows and bow out. So a due trigger enqueues EXACTLY
        // one run — no separate lease row or row lock needed. (Also replaces the
        // old read-modify-SaveChanges, which had no concurrency guard: both
        // replicas' writes silently succeeded and both went on to enqueue.)
        var observed = trigger.NextRunAt;
        int claimed;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            claimed = await db.WorkflowTriggers
                .Where(t => t.WorkflowTriggerId == trigger.WorkflowTriggerId
                            && t.NextRunAt == observed)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.NextRunAt, next)
                    .SetProperty(t => t.LastRunAt, now)
                    .SetProperty(t => t.UpdatedAt, now), ct);
        }

        if (claimed == 0)
        {
            // Another instance advanced this occurrence first — it owns the run.
            // Bowing out here is what guarantees exactly-once under concurrency.
            _logger.LogDebug(
                "scheduler.fire.claim_lost trigger_id={Id} — another instance owns this occurrence",
                trigger.WorkflowTriggerId);
            return;
        }

        if (next is null)
        {
            // A valid cron with no future occurrence, or an expression that went
            // invalid since creation. We won the claim, so exactly one instance
            // records the terminal status and stops the loop loudly.
            var reason = cronError is null ? "no_next_occurrence" : "cron_invalid";
            await StampStatusAsync(trigger.WorkflowTriggerId, reason, ct);
            _logger.LogWarning(
                "scheduler.fire.skipped trigger_id={Id} reason={Reason}",
                trigger.WorkflowTriggerId, reason);
            return;
        }

        try
        {
            var request = new RunWorkflowRequest
            {
                Input = trigger.InputDefaults.ValueKind == JsonValueKind.Object
                    ? trigger.InputDefaults
                    : JsonDocument.Parse("{}").RootElement,
                TargetDevices = trigger.TargetDevices,
            };

            // System-initiated run: userId = Guid.Empty (CreatedBy is a string
            // column, no FK), trigger tag = "schedule" for run provenance.
            var runId = await _executor.EnqueueRunAsync(Guid.Empty, trigger.WorkflowId, request, ct, trigger: "schedule");

            await StampStatusAsync(trigger.WorkflowTriggerId, "enqueued", ct);
            _logger.LogInformation(
                "scheduler.fire.ok trigger_id={Id} workflow_id={WorkflowId} run_id={RunId} next_run_at={Next:o}",
                trigger.WorkflowTriggerId, trigger.WorkflowId, runId, next);
        }
        catch (Exception ex)
        {
            // EnqueueRunAsync throws WorkflowExecutorException for pre-flight
            // failures (env mismatch, missing integration creds, structural
            // errors). Record it and move on — NextRunAt already advanced, so
            // the schedule retries at its next slot instead of hot-looping.
            await StampStatusAsync(trigger.WorkflowTriggerId, "enqueue_failed", ct);
            _logger.LogError(ex,
                "scheduler.fire.enqueue_failed trigger_id={Id} workflow_id={WorkflowId}",
                trigger.WorkflowTriggerId, trigger.WorkflowId);
        }
    }

    private async Task BackfillNextRunAtAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stale = await db.WorkflowTriggers
            .Where(t => t.IsActive && t.Enabled
                && t.NextRunAt == null
                && (t.Type == "cron" || t.Type == "schedule")
                && t.CronExpression != null)
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        var activated = 0;
        foreach (var t in stale)
        {
            var next = CronSchedule.ComputeNextUtc(t.CronExpression, t.Timezone, now, out _);
            if (next is null) continue;
            t.NextRunAt = next;
            t.UpdatedAt = now;
            activated++;
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("scheduler.backfill.ok activated={Count}", activated);
    }

    private async Task StampStatusAsync(Guid triggerId, string status, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var t = await db.WorkflowTriggers.FirstOrDefaultAsync(
                x => x.WorkflowTriggerId == triggerId, ct);
            if (t is null) return;
            t.LastRunStatus = status;
            t.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "scheduler.stamp_status.failed trigger_id={Id}", triggerId);
        }
    }

    private static TimeSpan ResolvePollInterval()
    {
        var raw = Environment.GetEnvironmentVariable("Scheduler__PollSeconds");
        return double.TryParse(raw, out var seconds) && seconds >= 1
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(10);
    }
}
