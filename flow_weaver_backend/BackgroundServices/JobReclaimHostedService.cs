using flow_weaver_backend.Services.Engine;

namespace flow_weaver_backend.BackgroundServices;

// Periodically returns claimed jobs whose lease has expired to the pending
// pool. Without this, a worker crash between Claim and Complete/Fail leaves
// the job stranded in 'claimed' forever and its run hangs.
//
// Runs in every process that loads the hosted services (API and worker-only
// mode alike). The reclaim SQL is idempotent and uses a single UPDATE, so
// multiple replicas stepping on each other costs nothing beyond duplicate
// no-op writes.
public sealed class JobReclaimHostedService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobReclaimHostedService> _logger;

    public JobReclaimHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<JobReclaimHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "JobReclaim started — sweep every {Interval}s", (int)SweepInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IQueueRepository>();
                var released = await queue.ReclaimExpiredAsync(stoppingToken);
                if (released > 0)
                    _logger.LogWarning(
                        "JobReclaim released {Count} expired claims back to pending", released);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JobReclaim sweep failed — retrying next cycle");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); } catch { break; }
        }

        _logger.LogInformation("JobReclaim shutting down");
    }
}
