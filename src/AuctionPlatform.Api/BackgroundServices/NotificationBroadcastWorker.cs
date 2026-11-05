using System.Threading.Channels;
using AuctionPlatform.Application.Notifications;

namespace AuctionPlatform.Api.BackgroundServices;

/// <summary>
/// Holds notifications meant for every user until the worker sends them. Sending to thousands of
/// phones takes minutes, which an admin's request must not wait for.
///
/// The queue lives in memory: a message still waiting when the site restarts is lost, which is
/// logged by the worker as it stops.
/// </summary>
public sealed class NotificationBroadcastQueue : INotificationBroadcaster
{
    private readonly Channel<NotificationTemplate> _channel =
        Channel.CreateUnbounded<NotificationTemplate>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<NotificationTemplate> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(NotificationTemplate template, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(template, ct);
}

/// <summary>Sends what <see cref="NotificationBroadcastQueue"/> holds, one message at a time.</summary>
public sealed class NotificationBroadcastWorker : BackgroundService
{
    private readonly NotificationBroadcastQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationBroadcastWorker> _logger;

    public NotificationBroadcastWorker(
        NotificationBroadcastQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationBroadcastWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var template in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

                    var users = await notifications.NotifyEveryoneAsync(template, stoppingToken);
                    _logger.LogInformation("Sent {Type} notification to {Users} users.", template.Type, users);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sending {Type} notification to everyone failed.", template.Type);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            if (_queue.Reader.Count > 0)
            {
                _logger.LogWarning("{Count} notification(s) for everyone were not sent before shutdown.",
                    _queue.Reader.Count);
            }
        }
    }
}
