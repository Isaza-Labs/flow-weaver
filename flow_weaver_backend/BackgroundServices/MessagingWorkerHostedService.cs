using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Messaging;

namespace flow_weaver_backend.BackgroundServices;

// Polls the shared job queue for "messaging" jobs and dispatches them to
// IMessagingJobProcessor. Separate from WorkerHostedService (which runs
// workflow/step jobs) so agent turns from external channels don't compete with
// device-automation work for the same worker slots. Lease reclaim is handled by
// the shared JobReclaimHostedService.
public sealed class MessagingWorkerHostedService : BackgroundService
{
    private static readonly string[] Tags = { MessagingJobTypes.Tag };
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MessagingWorkerHostedService> _logger;
    private readonly string _workerId;

    public MessagingWorkerHostedService(
        IServiceScopeFactory scopeFactory, ILogger<MessagingWorkerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _workerId = $"{Environment.MachineName}-msg-{Environment.ProcessId}-{Guid.NewGuid():N}"[..40];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MessagingWorker started worker_id={WorkerId}", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var queue = scope.ServiceProvider.GetRequiredService<IQueueRepository>();

                Job? job = await queue.ClaimAsync(Tags, _workerId, stoppingToken);
                if (job is null)
                {
                    await Task.Delay(PollDelay, stoppingToken);
                    continue;
                }

                var processor = scope.ServiceProvider.GetRequiredService<IMessagingJobProcessor>();
                try
                {
                    await processor.ProcessAsync(job, stoppingToken);
                    await queue.CompleteAsync(job.JobId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "messaging.worker.job_failed job={Job} type={Type}", job.JobId, job.Type);
                    try { await queue.FailAsync(job.JobId, ex.Message, CancellationToken.None); }
                    catch { /* best effort */ }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "messaging.worker.loop_error — backing off");
                try { await Task.Delay(PollDelay, stoppingToken); } catch { break; }
            }
        }

        _logger.LogInformation("MessagingWorker shutting down worker_id={WorkerId}", _workerId);
    }
}
