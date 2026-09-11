using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Utils.Report;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.BackgroundServices;

// Two-pass sweeper that runs every hour:
//   Pass 1 — soft-delete rows past ExpiresAt (sets IsActive = false).
//   Pass 2 — hard-delete rows soft-deleted more than
//            SoftDeleteGraceDays ago, fully removing the blob.
//
// Each pass emits a `system` trace so /admin/traces shows the purge as
// it happens. Failures are logged but never thrown — a broken retention
// loop must never take down the main API process.
public sealed class ReportRetentionHostedService : BackgroundService
{
    // Scan interval. Tight enough to be responsive after config changes,
    // loose enough to avoid pointless wakeups on an idle deployment.
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ReportOptions _options;
    private readonly ILogger<ReportRetentionHostedService> _logger;

    public ReportRetentionHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<ReportOptions> options,
        ILogger<ReportRetentionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First tick after a short delay so we don't race with startup seeding.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "report.retention.sweep_failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var trace = scope.ServiceProvider.GetRequiredService<ITraceLogger>();

        var now = DateTime.UtcNow;
        var graceCutoff = now.AddDays(-_options.SoftDeleteGraceDays);

        // Pass 1: soft-delete expired rows.
        var softDeleted = await db.ReportArtifacts
            .Where(r => r.IsActive && r.ExpiresAt < now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.IsActive, false)
                      .SetProperty(r => r.UpdatedAt, now),
                ct);

        // Pass 2: hard-delete rows soft-deleted beyond the grace window.
        // Executes as a single DELETE thanks to ExecuteDeleteAsync.
        var hardDeleted = await db.ReportArtifacts
            .Where(r => !r.IsActive && r.UpdatedAt < graceCutoff)
            .ExecuteDeleteAsync(ct);

        if (softDeleted > 0 || hardDeleted > 0)
        {
            _logger.LogInformation(
                "report.retention.swept soft={Soft} hard={Hard}",
                softDeleted, hardDeleted);

            await trace.EventAsync("report.retention.sweep", "system", "completed",
                metadata: new { soft_deleted = softDeleted, hard_deleted = hardDeleted },
                ct: ct);
        }
    }
}
