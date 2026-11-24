using System.Collections.Concurrent;
using System.Diagnostics;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;

namespace AuctionPlatform.Application.Bidding;

/// <summary>
/// Bids and joins on one auction take turns here, in memory, before they touch the database.
///
/// Every bid and every join locks its auction's row in PostgreSQL, so on one auction they can only
/// ever go one at a time. Before this queue, each waiting request did its waiting inside the
/// database, holding a pooled connection the whole time. With a few hundred people bidding, every
/// connection in the pool was held by requests queued behind the same auction, and everything else
/// that needed the database — the leaderboard, snapshots, other auctions, the store, and the
/// auction's own closing — waited behind them (load test of 23 Nov 2026: up to 300 requests waiting
/// for a connection, winners shown 11–25 s after the close).
///
/// Now a request waits here holding nothing — no connection, no thread — and takes a connection
/// only when it is its turn. Each auction has its own line, so a busy auction never delays another.
///
/// The database lock is unchanged and remains the guarantee. This queue lives in one process: a
/// second server instance would have its own, and PostgreSQL would still keep every bid correct,
/// only less efficiently. Nothing here decides whether a bid is valid.
///
/// Limits, so the line can never grow without end:
/// <list type="bullet">
/// <item>at most <c>maxWaiting</c> requests wait per auction; beyond that a request is refused at
///   once with <see cref="ErrorCodes.AuctionBusy"/>;</item>
/// <item>a request waits at most <c>maxWait</c>, well inside the app's 20 second timeout, then is
///   refused the same way. Either way it never reached the database, so the answer is certain:
///   the bid was not placed;</item>
/// <item>when the server is shutting down, waiting requests are released with
///   <see cref="ErrorCodes.ServiceUnavailable"/> and new ones are refused; a request that already
///   has its turn finishes normally;</item>
/// <item>a request whose caller gives up (the connection drops) leaves without ever taking a turn.</item>
/// </list>
///
/// It also remembers auctions that can never take another bid, so the late taps after a close are
/// refused at once instead of each opening a transaction to find out.
/// </summary>
public interface IAuctionQueue
{
    /// <summary>
    /// Waits for this auction's turn, first come first served. Dispose the result to pass the turn
    /// on. Throws <see cref="DomainException"/> when refused (busy, shutting down) and
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    ValueTask<IDisposable> EnterAsync(Guid auctionId, string operation, CancellationToken ct);

    /// <summary>The refusal to give straight away, when the auction can never take another bid.</summary>
    DomainException? RefusalFor(Guid auctionId);

    /// <summary>
    /// Records that the auction can never take another bid — it finished, was cancelled, reached
    /// its target, or its time ran out under a policy that does not extend it — and how to say so.
    /// </summary>
    void MarkClosed(Guid auctionId, string code, string message);

    /// <summary>The server is stopping: release everyone waiting and admit nobody new.</summary>
    void Close();
}

public sealed class AuctionQueue : IAuctionQueue
{
    public const int DefaultMaxWaiting = 1000;
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RememberClosedFor = TimeSpan.FromHours(6);

    private readonly int _maxWaiting;
    private readonly TimeSpan _maxWait;
    private readonly ConcurrentDictionary<Guid, Line> _lines = new();
    private readonly ConcurrentDictionary<Guid, Closed> _closed = new();
    private readonly CancellationTokenSource _stopping = new();

    public AuctionQueue(int maxWaitingPerAuction = DefaultMaxWaiting, TimeSpan? maxWait = null)
    {
        _maxWaiting = Math.Max(1, maxWaitingPerAuction);
        _maxWait = maxWait ?? DefaultMaxWait;
    }

    /// <summary>Auctions with a request waiting or being served. Zero when idle: nothing is kept.</summary>
    public int ActiveLines => _lines.Count;

    public async ValueTask<IDisposable> EnterAsync(Guid auctionId, string operation, CancellationToken ct)
    {
        // A join takes a wallet charge with it and a crowd can arrive at once before the start, so
        // joins may wait three times as long as bids before being sent away.
        var maxWait = operation == "join" ? _maxWait * 3 : _maxWait;
        var tag = new KeyValuePair<string, object?>("operation", operation);

        if (_stopping.IsCancellationRequested) throw Refuse(operation, "shutdown");

        var line = Join(auctionId);
        if (line is null) throw Refuse(operation, "full");

        AppMetrics.AuctionQueueLength.Add(1, tag);
        var started = Stopwatch.GetTimestamp();

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        wait.CancelAfter(maxWait);

        try
        {
            await line.Gate.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            Leave(auctionId, line);

            // The caller gave up: let them see it as such. Otherwise this queue sent them away.
            if (ct.IsCancellationRequested) throw;
            throw Refuse(operation, _stopping.IsCancellationRequested ? "shutdown" : "timeout");
        }
        finally
        {
            AppMetrics.AuctionQueueLength.Add(-1, tag);
        }

        AppMetrics.AuctionQueueWait.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tag);
        return new Turn(this, auctionId, line, operation);
    }

    private static DomainException Refuse(string operation, string reason)
    {
        AppMetrics.AuctionQueueRefused.Add(1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("reason", reason));

        return reason == "shutdown"
            ? new DomainException(ErrorCodes.ServiceUnavailable,
                "The server is restarting. Your bid was not placed; please try again in a moment.")
            : new DomainException(ErrorCodes.AuctionBusy,
                "Too many bids are waiting on this auction right now. Your bid was not placed; please try again.");
    }

    public DomainException? RefusalFor(Guid auctionId) =>
        _closed.TryGetValue(auctionId, out var closed) ? new DomainException(closed.Code, closed.Message) : null;

    public void MarkClosed(Guid auctionId, string code, string message)
    {
        var now = DateTime.UtcNow;
        _closed[auctionId] = new Closed(code, message, now);

        // A few hundred closed auctions a day at most; forget the old ones, the database still knows.
        if (_closed.Count > 512)
        {
            foreach (var (id, closed) in _closed)
            {
                if (now - closed.At > RememberClosedFor) _closed.TryRemove(id, out _);
            }
        }
    }

    public void Close() => _stopping.Cancel();

    /// <summary>Joins the auction's line, or returns null when it is already full.</summary>
    private Line? Join(Guid auctionId)
    {
        while (true)
        {
            var line = _lines.GetOrAdd(auctionId, static _ => new Line());
            lock (line)
            {
                if (line.People >= 0)
                {
                    // People counts the one being served too, so this many may be waiting.
                    if (line.People > _maxWaiting) return null;
                    line.People++;
                    return line;
                }
            }
            // That line was retired between the lookup and the lock; a fresh one takes its place.
        }
    }

    private void Leave(Guid auctionId, Line line)
    {
        lock (line)
        {
            if (--line.People > 0) return;

            // Nobody waiting and nobody served: retire it, so idle auctions cost nothing.
            line.People = -1;
            _lines.TryRemove(new KeyValuePair<Guid, Line>(auctionId, line));
        }
    }

    private sealed class Line
    {
        public readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>Waiting or being served; -1 once retired.</summary>
        public int People;
    }

    private sealed record Closed(string Code, string Message, DateTime At);

    /// <summary>One request's turn. Passing it on also records how long it was held.</summary>
    private sealed class Turn : IDisposable
    {
        private AuctionQueue? _queue;
        private readonly Guid _auctionId;
        private readonly Line _line;
        private readonly string _operation;
        private readonly long _started = Stopwatch.GetTimestamp();

        public Turn(AuctionQueue queue, Guid auctionId, Line line, string operation)
        {
            _queue = queue;
            _auctionId = auctionId;
            _line = line;
            _operation = operation;
        }

        public void Dispose()
        {
            var queue = Interlocked.Exchange(ref _queue, null);
            if (queue is null) return;

            AppMetrics.AuctionTurnTime.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds,
                new KeyValuePair<string, object?>("operation", _operation));
            _line.Gate.Release();
            queue.Leave(_auctionId, _line);
        }
    }
}
