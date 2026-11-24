using System.Diagnostics.Metrics;

namespace AuctionPlatform.Application.Common;

/// <summary>
/// The platform's own measurements, for the admin runtime diagnostics and the bidding load test.
///
/// Recording into these costs a few hundred nanoseconds and changes nothing about what the code
/// does: they only say how long the database took to hand over a connection and to give a bid or
/// a join the auction's row lock, which no outside tool can see.
/// </summary>
public static class AppMetrics
{
    public const string MeterName = "AuctionPlatform";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Time for EF Core to open a connection: the wait for a free one in the pool, plus creating a
    /// new one when the pool has none ready.
    /// </summary>
    public static readonly Histogram<double> ConnectionOpenTime =
        Meter.CreateHistogram<double>("app.db.connection.open_time", "s");

    /// <summary>
    /// Time to obtain an auction's row lock (SELECT ... FOR UPDATE), tagged with the operation.
    /// Grows when bids or joins queue behind each other on one auction.
    /// </summary>
    public static readonly Histogram<double> AuctionLockWait =
        Meter.CreateHistogram<double>("app.auction.lock_wait", "s");

    /// <summary>
    /// How long a bid keeps the auction's row lock, from getting it to COMMIT. Bids on one auction
    /// take turns on that lock, so one auction can accept at most about 1 / this many bids a second.
    /// </summary>
    public static readonly Histogram<double> BidLockHeld =
        Meter.CreateHistogram<double>("app.bid.lock_held", "s");

    /// <summary>
    /// Time a bid or join waited for its auction's turn in the server's own queue, before it
    /// touched the database (see Bidding.AuctionQueue).
    /// </summary>
    public static readonly Histogram<double> AuctionQueueWait =
        Meter.CreateHistogram<double>("app.auction.queue_wait", "s");

    /// <summary>Bids or joins waiting in an auction's queue right now, tagged with the operation.</summary>
    public static readonly UpDownCounter<int> AuctionQueueLength =
        Meter.CreateUpDownCounter<int>("app.auction.queue_length", "{request}");

    /// <summary>Bids refused at the queue because their auction can never take another bid.</summary>
    public static readonly Counter<int> BidsRefusedClosed =
        Meter.CreateCounter<int>("app.bid.refused_closed", "{bid}");

    /// <summary>
    /// Requests the queue sent away before they reached the database, tagged with the operation
    /// and the reason: "full", "timeout" or "shutdown".
    /// </summary>
    public static readonly Counter<int> AuctionQueueRefused =
        Meter.CreateCounter<int>("app.auction.queue_refused", "{request}");

    /// <summary>
    /// How long a bid or join kept its auction's turn: the transaction plus starting the live
    /// updates. Bids on one auction can be served at most about 1 / this many a second.
    /// </summary>
    public static readonly Histogram<double> AuctionTurnTime =
        Meter.CreateHistogram<double>("app.auction.turn_time", "s");

    public static void RecordLockWait(string operation, TimeSpan elapsed) =>
        AuctionLockWait.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("operation", operation));
}
