using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Slo;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.BackgroundServices;

// Daily SLO sweep. Runs SloComputeService against the
// last 7 days, and for each metric whose value crosses its target,
// writes a single AuditEvent row with action `slo.breach.<metric_key>`.
//
// The audit row is the alert primitive: it shows up in /admin/audit and
// can be filtered via `action_prefix=slo.breach` for compliance digests.
// External notification (email / Slack / pager) is intentionally NOT
// triggered here — that wiring lives in the deployment's alerting
// integration. The audit row is the durable signal that survives outages.
//
// Registered ONLY in API mode so the backend container is the unique
// owner; without that gate the worker would emit duplicate breach rows.
public sealed class SloBreachWatcherService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);
    private const int Window = 7;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SloBreachWatcherService> _logger;

    public SloBreachWatcherService(
        IServiceScopeFactory scopeFactory,
        ILogger<SloBreachWatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial settle so boot logs and migrations finish before we
        // start writing audit rows.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "slo.breach.sweep_failed");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var compute = scope.ServiceProvider.GetRequiredService<SloComputeService>();

        var totalBreaches = 0;
        var now = DateTime.UtcNow;

        SloSnapshot snapshot;
        try
        {
            snapshot = await compute.ComputeAsync(Window, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "slo.breach.compute_failed");
            return;
        }

        foreach (var entry in snapshot.Slos)
        {
            if (!SloComputeService.IsBreach(entry)) continue;

            db.AuditLogs.Add(new AuditEvent
            {
                AuditEventId = Guid.NewGuid(),
                UserId = null,
                // Same convention as "workflow-runner" / "git-webhook": the
                // Actor names the automation, so the audit UI shows WHO
                // (the platform's SLO sweep) instead of a blank user. Ip
                // stays null on purpose — there is no network caller.
                Actor = "slo-watcher",
                EntityType = "slo",
                EntityId = null,
                Action = $"slo.breach.{entry.Key}",
                BeforeJson = JsonDocument.Parse("null").RootElement,
                AfterJson = JsonSerializer.SerializeToElement(new
                {
                    key = entry.Key,
                    label = entry.Label,
                    unit = entry.Unit,
                    value = entry.Value,
                    target = entry.Target,
                    better = entry.Better,
                    window_days = snapshot.Days,
                    window_from = snapshot.From,
                }),
                Ip = null,
                UserAgent = "SloBreachWatcherService",
                At = now,
                RequestId = null,
            });
            totalBreaches++;
        }

        if (totalBreaches > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogWarning(
                "slo.breach.sweep_emitted breaches={Breaches}", totalBreaches);
        }
        else
        {
            _logger.LogDebug("slo.breach.sweep_clean");
        }
    }
}
