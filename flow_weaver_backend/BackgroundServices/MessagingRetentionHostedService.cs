using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Messaging;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.BackgroundServices;

// Periodically prunes messaging audit rows (inbound events + outbound
// deliveries) older than MessagingOptions.RetentionDays and deletes
// past-expiry account-linking tokens. Mirrors TraceRetentionHostedService;
// the deletes are set-based (ExecuteDelete) so they don't materialize rows.
public sealed class MessagingRetentionHostedService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MessagingOptions _options;
    private readonly ILogger<MessagingRetentionHostedService> _logger;

    public MessagingRetentionHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<MessagingOptions> options,
        ILogger<MessagingRetentionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RetentionDays <= 0)
        {
            _logger.LogInformation("MessagingRetention disabled (RetentionDays <= 0)");
            return;
        }

        _logger.LogInformation(
            "MessagingRetention started — keep {Days}d, sweep every {Hours}h",
            _options.RetentionDays, (int)SweepInterval.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var cutoff = now.AddDays(-_options.RetentionDays);

                await using var scope = _scopeFactory.CreateAsyncScope();
                var audit = scope.ServiceProvider.GetRequiredService<IMessagingDeliveryRepository>();
                var tokens = scope.ServiceProvider.GetRequiredService<IMessagingLinkTokenRepository>();

                var inbound = await audit.DeleteInboundOlderThanAsync(cutoff, stoppingToken);
                var outbound = await audit.DeleteDeliveriesOlderThanAsync(cutoff, stoppingToken);
                var expired = await tokens.DeleteExpiredOlderThanAsync(now, stoppingToken);

                if (inbound + outbound + expired > 0)
                    _logger.LogInformation(
                        "MessagingRetention swept inbound={Inbound} outbound={Outbound} tokens={Tokens}",
                        inbound, outbound, expired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MessagingRetention sweep failed — retrying next cycle");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); } catch { break; }
        }

        _logger.LogInformation("MessagingRetention shutting down");
    }
}
