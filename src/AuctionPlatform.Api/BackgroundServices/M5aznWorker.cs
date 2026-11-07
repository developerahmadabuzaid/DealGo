using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.M5azn;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Api.BackgroundServices;

/// <summary>
/// Sends paid store orders with M5azn products, follows them, and copies M5azn's stock (docs/M5AZN.md).
/// M5azn documents no webhooks, so following an order means asking about it now and then.
/// It keeps running while M5azn is switched off, so switching it on from the dashboard takes
/// effect within one interval; while off, each pass only reads the settings.
/// </summary>
public sealed class M5aznWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly M5aznOptions _options;
    private readonly ILogger<M5aznWorker> _logger;
    private readonly IM5aznAccountProvider _account;
    private DateTime _nextStockSync = DateTime.MinValue;
    private bool? _wasActive;

    public M5aznWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<M5aznOptions> options,
        IM5aznAccountProvider account,
        ILogger<M5aznWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _account = account;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_options.WorkerIntervalSeconds, 30, 3600)));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var account = await _account.RefreshAsync(ct: stoppingToken);
                if (account.IsActive != _wasActive)
                {
                    _wasActive = account.IsActive;
                    if (account.IsActive)
                    {
                        _logger.LogInformation("M5azn is on: {BaseUrl}, automatic sending {Auto}, stock sync {Stock}, settings from {Source}.",
                            account.BaseUrl, account.AutoSubmit ? "on" : "off", _options.StockSyncEnabled ? "on" : "off", account.Source);
                    }
                    else
                    {
                        _logger.LogInformation("M5azn is switched off or has no seller account. Nothing is sent to it.");
                    }
                }

                if (!account.IsActive) continue;

                using var scope = _scopeFactory.CreateScope();
                var locks = scope.ServiceProvider.GetRequiredService<IDistributedLockService>();

                await using var handle = await locks.AcquireAsync("m5azn:worker", TimeSpan.FromMinutes(10), stoppingToken);
                if (handle is null) continue;

                var service = scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>();
                await service.RunAsync(stoppingToken);

                if (_options.StockSyncEnabled && DateTime.UtcNow >= _nextStockSync)
                {
                    _nextStockSync = DateTime.UtcNow.AddMinutes(Math.Max(5, _options.StockSyncMinutes));
                    await service.SyncStockAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "M5azn pass failed; it runs again shortly.");
            }
        }
    }
}
