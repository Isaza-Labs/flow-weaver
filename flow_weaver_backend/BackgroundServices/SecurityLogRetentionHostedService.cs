using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.BackgroundServices;

// Retention for the two security logs that had none: auth_events and
// audit_logs. trace_events, reports and messaging deliveries were already
// swept; these two grew forever.
//
// They get opposite defaults, because they are not the same kind of record:
//
//   auth_events (Auth:RetentionDays, default 180)
//     Volume is session-shaped, not event-shaped — one row per login, per
//     logout, per LOCKOUT, and per token refresh, so an active user produces
//     dozens a day on refresh alone. Six months is well past the window in
//     which a failed-login pattern is still actionable.
//
//   audit_logs (Audit:RetentionDays, default 0 = keep forever)
//     The compliance record of who changed what. Deleting it by default would
//     be the wrong call for anyone who has to answer that question about last
//     year, so the sweeper is off unless an operator opts in — normally to
//     satisfy a data-retention policy that requires deletion rather than to
//     save space.
//
// Runs once on boot then every 6 hours, mirroring TraceRetentionHostedService.
public sealed class SecurityLogRetentionHostedService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private const int DefaultAuthRetentionDays = 180;
    private const int DefaultAuditRetentionDays = 0; // 0 / negative => never delete

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<SecurityLogRetentionHostedService> _logger;

    public SecurityLogRetentionHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<SecurityLogRetentionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var authDays = _config.GetValue("Auth:RetentionDays", DefaultAuthRetentionDays);
        var auditDays = _config.GetValue("Audit:RetentionDays", DefaultAuditRetentionDays);

        _logger.LogInformation(
            "security_log.retention.started auth_days={AuthDays} audit_days={AuditDays} "
            + "sweep_interval={IntervalHours}h",
            authDays > 0 ? authDays : 0,
            auditDays > 0 ? auditDays : 0,
            (int)SweepInterval.TotalHours);

        if (authDays <= 0 && auditDays <= 0)
        {
            _logger.LogInformation("security_log.retention.disabled — both windows are unlimited");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                if (authDays > 0)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-authDays);
                    var deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                        $"DELETE FROM auth_events WHERE \"At\" < {cutoff}",
                        stoppingToken);
                    if (deleted > 0)
                        _logger.LogInformation(
                            "auth_event.retention.sweep deleted={Deleted} cutoff={Cutoff:O}",
                            deleted, cutoff);
                }

                if (auditDays > 0)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-auditDays);
                    var deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                        $"DELETE FROM audit_logs WHERE \"At\" < {cutoff}",
                        stoppingToken);
                    // Logged at Warning, not Information: this is the audit
                    // trail being destroyed on purpose, and it should be
                    // obvious in the log that someone configured it.
                    if (deleted > 0)
                        _logger.LogWarning(
                            "audit_log.retention.sweep deleted={Deleted} cutoff={Cutoff:O} retention_days={Days}",
                            deleted, cutoff, auditDays);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "security_log.retention.sweep.failed");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); } catch { break; }
        }
    }
}
