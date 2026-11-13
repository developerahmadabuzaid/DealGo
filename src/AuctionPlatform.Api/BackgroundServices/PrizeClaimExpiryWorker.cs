using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Prizes;

namespace AuctionPlatform.Api.BackgroundServices;

/// <summary>
/// Once a minute, closes the prize claims whose winners did not pay in time and returns their entry
/// fee to the Shopping wallet (<see cref="PrizeClaimExpiryService"/>).
/// </summary>
public sealed class PrizeClaimExpiryWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PrizeClaimExpiryWorker> _logger;

    public PrizeClaimExpiryWorker(IServiceScopeFactory scopeFactory, ILogger<PrizeClaimExpiryWorker> logger)
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
                var locks = scope.ServiceProvider.GetRequiredService<IDistributedLockService>();
                await using var handle = await locks.AcquireAsync("prizes:expiry", TimeSpan.FromMinutes(5), stoppingToken);
                if (handle is null) continue;

                await scope.ServiceProvider.GetRequiredService<IPrizeClaimExpiryService>().ExpireOverdueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Expiring unpaid prize claims failed; it is tried again shortly.");
            }
        }
    }
}
