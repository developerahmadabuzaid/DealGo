using System.Threading.Channels;
using AuctionPlatform.Application.M5azn;

namespace AuctionPlatform.Api.BackgroundServices;

/// <summary>Holds the one import an admin asked for until the worker starts it.</summary>
public sealed class M5aznImportQueue : IM5aznImportQueue
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true });

    public ChannelReader<bool> Reader => _channel.Reader;

    public bool TryRequest() => _channel.Writer.TryWrite(true);
}

/// <summary>Runs the imports asked for with the dashboard's button, apart from the admin's request.</summary>
public sealed class M5aznImportWorker : BackgroundService
{
    private readonly M5aznImportQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<M5aznImportWorker> _logger;

    public M5aznImportWorker(M5aznImportQueue queue, IServiceScopeFactory scopeFactory, ILogger<M5aznImportWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var _ in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IM5aznCatalogService>().RunRequestedImportAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // The status the page reads already says so.
                    _logger.LogWarning(ex, "M5azn: the import asked for in the dashboard failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The site is stopping.
        }
    }
}
