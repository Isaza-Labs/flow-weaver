using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace flow_weaver_backend.BackgroundServices;

// Periodically lists every Integration with AllowPrivateNetwork=true and
// emits a single audit row snapshotting the active opt-outs.
// The point is not to detect change (the IntegrationService already audits
// each transition) but to give compliance a recurring check that the set
// of integrations with the SSRF-guard relaxed is what they expect.
//
// Runs once on boot (so a fresh deployment immediately surfaces existing
// opt-outs) and then every 24 hours.
public sealed class AllowPrivateNetworkReminderService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AllowPrivateNetworkReminderService> _logger;

    public AllowPrivateNetworkReminderService(
        IServiceScopeFactory scopeFactory,
        ILogger<AllowPrivateNetworkReminderService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Tiny initial delay so boot logs settle and migrations finish.
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "audit.allow_private_network.sweep_failed");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.Integrations
            .AsNoTracking()
            .Where(i => i.IsActive && i.AllowPrivateNetwork)
            .Select(i => new { i.IntegrationId, i.Name, i.Type, i.BaseURL })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            _logger.LogDebug("audit.allow_private_network.sweep no_active_optouts");
            return;
        }

        var now = DateTime.UtcNow;

        {
            var snapshot = new
            {
                count = rows.Count,
                integrations = rows.Select(g => new
                {
                    integration_id = g.IntegrationId,
                    name = g.Name,
                    type = g.Type,
                    base_url = g.BaseURL,
                }).ToArray(),
            };

            db.AuditLogs.Add(new AuditEvent
            {
                AuditEventId = Guid.NewGuid(),
                UserId = null,
                // Same convention as "workflow-runner" / "git-webhook": the
                // Actor names the automation, so the audit UI shows WHO
                // (the daily compliance sweep) instead of a blank user. Ip
                // stays null on purpose — there is no network caller.
                Actor = "compliance-reminder",
                EntityType = "integration",
                EntityId = null,
                Action = "allow_private_network.reminder",
                BeforeJson = JsonDocument.Parse("null").RootElement,
                AfterJson = JsonSerializer.SerializeToElement(snapshot),
                Ip = null,
                UserAgent = "AllowPrivateNetworkReminderService",
                At = now,
                RequestId = null,
            });
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "audit.allow_private_network.sweep_ok integration_count={IntegrationCount}",
            rows.Count);
    }
}
