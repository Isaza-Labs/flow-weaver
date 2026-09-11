using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.BackgroundServices;

// Deletes trace_events rows older than Tracing:RetentionDays (default 365).
// Configurable at deploy time via the Tracing__RetentionDays env var (wired
// from TRACE_RETENTION_DAYS in deploy/.env) or appsettings. Without this the
// table grows unbounded — we emit one row for every chat turn, every workflow
// run, every tool call. A 365-day window balances "enough forensic depth to
// diagnose a reported bug" with "doesn't dominate the DB footprint".
//
// Runs once on boot (so a long downtime doesn't let the table spike) and
// then every 6 hours. Uses raw SQL DELETE with a time predicate to avoid
// loading rows into EF just to drop them.
public sealed class TraceRetentionHostedService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private const int DefaultRetentionDays = 365;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<TraceRetentionHostedService> _logger;

    public TraceRetentionHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<TraceRetentionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var days = Math.Max(1, _config.GetValue("Tracing:RetentionDays", DefaultRetentionDays));
        _logger.LogInformation(
            "trace.retention.started days={Days} sweep_interval={IntervalMinutes}m",
            days, (int)SweepInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // The timestamp lives in a column — we use raw SQL rather
                // than EF's ExecuteDelete because this project pins older
                // EF versions that need parameterized deletes via FromSql.
                var cutoff = DateTime.UtcNow.AddDays(-days);
                var deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM trace_events WHERE \"At\" < {cutoff}",
                    stoppingToken);

                if (deleted > 0)
                    _logger.LogInformation(
                        "trace.retention.sweep deleted={Deleted} cutoff={Cutoff:O}",
                        deleted, cutoff);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "trace.retention.sweep.failed");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); } catch { break; }
        }
    }
}
