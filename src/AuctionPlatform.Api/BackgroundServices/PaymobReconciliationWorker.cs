using AuctionPlatform.Application.Common;
using AuctionPlatform.Infrastructure.Services;

namespace AuctionPlatform.Api.BackgroundServices;

/// <summary>
/// Settles Paymob top-ups that no callback settled: a verified callback the server recorded but
/// failed to apply (a database hiccup, a restart in between), and a top-up still open a few
/// minutes after it was started (a callback lost on the way), which is checked with Paymob's
/// inquiry API. Nothing is credited without Paymob's own confirmation.
/// </summary>
public sealed class PaymobReconciliationWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymobReconciliationWorker> _logger;

    public PaymobReconciliationWorker(IServiceScopeFactory scopeFactory, ILogger<PaymobReconciliationWorker> logger)
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

                await using var handle = await locks.AcquireAsync("paymob:reconcile", TimeSpan.FromMinutes(5), stoppingToken);
                if (handle is null) continue;

                await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>().ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Paymob reconciliation failed; it runs again shortly.");
            }
        }
    }
}
