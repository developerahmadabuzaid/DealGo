using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Notifications;

namespace AuctionPlatform.Api.BackgroundServices;

/// <summary>Sends the messages staff scheduled in the dashboard, within half a minute of their time.</summary>
public sealed class ScheduledNotificationWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledNotificationWorker> _logger;

    public ScheduledNotificationWorker(IServiceScopeFactory scopeFactory, ILogger<ScheduledNotificationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();

                // One server at a time; each message is also claimed on its own, so this is only
                // to keep two servers from doing the same work.
                var locks = scope.ServiceProvider.GetRequiredService<IDistributedLockService>();
                await using var handle = await locks.AcquireAsync("notifications:scheduled", TimeSpan.FromMinutes(10), stoppingToken);
                if (handle is null) continue;

                await scope.ServiceProvider.GetRequiredService<IAdminNotificationService>().SendDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sending scheduled notifications failed; it is tried again shortly.");
            }
        }
    }
}
