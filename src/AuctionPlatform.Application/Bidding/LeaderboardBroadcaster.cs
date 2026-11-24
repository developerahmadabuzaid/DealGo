using System.Collections.Concurrent;
using AuctionPlatform.Application.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuctionPlatform.Application.Bidding;

/// <summary>Sends an auction's top ten to everyone watching it, a few times a second at most.</summary>
public interface ILeaderboardBroadcaster
{
    /// <summary>Marks the auction's leaderboard as changed. Returns at once.</summary>
    void Request(Guid auctionId);
}

/// <summary>
/// Coalesces leaderboard broadcasts.
///
/// Every bid used to query the top ten and broadcast it before answering the bidder. In a busy
/// auction that is five queries a second to a database that is slow to answer, each one keeping
/// a bidder's button spinning, and most of the lists were out of date before they arrived. Now a
/// bid only marks its auction as changed; every <see cref="Interval"/> one query per changed
/// auction sends the list as it stands. Price updates still go out with every bid.
/// </summary>
public sealed class LeaderboardBroadcaster : ILeaderboardBroadcaster, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(300);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LeaderboardBroadcaster> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _changed = new();
    private readonly Timer _timer;
    private int _running;

    public LeaderboardBroadcaster(IServiceScopeFactory scopes, ILogger<LeaderboardBroadcaster> logger)
    {
        _scopes = scopes;
        _logger = logger;
        _timer = new Timer(_ => _ = FlushAsync(), null, Interval, Interval);
    }

    public void Request(Guid auctionId) => _changed.TryAdd(auctionId, 0);

    private async Task FlushAsync()
    {
        // One flush at a time: a slow database must not pile up overlapping ones.
        if (_changed.IsEmpty || Interlocked.Exchange(ref _running, 1) == 1) return;

        try
        {
            foreach (var auctionId in _changed.Keys)
            {
                // Removed before the query, so a bid landing meanwhile marks it again for the next flush.
                if (!_changed.TryRemove(auctionId, out _)) continue;

                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<Common.IApplicationDbContext>();

                    // Read before the list, so the list includes at least this bid: a client keeps
                    // the list with the highest number and drops one that arrives late.
                    var lastSequence = await db.Auctions.AsNoTracking()
                        .Where(a => a.Id == auctionId).Select(a => a.LastSequenceNumber).FirstOrDefaultAsync();
                    var leaderboard = await BidService.ReadLeaderboardAsync(db, auctionId, 10, CancellationToken.None);

                    await scope.ServiceProvider.GetRequiredService<IAuctionRealtimeNotifier>()
                        .LeaderboardUpdatedAsync(auctionId, leaderboard, lastSequence, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Best effort, as before: clients resynchronise from the snapshot.
                    _logger.LogWarning(ex, "Failed to broadcast the leaderboard for auction {AuctionId}.", auctionId);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
