using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionLoadTest;

/// <summary>A bid the server accepted, as anyone could learn it: from the bidder's answer or the broadcast.</summary>
sealed record BidInfo(long Sequence, decimal Price, DateTime ServerTimestamp, Guid ParticipantId);

/// <summary>One bid attempt by one simulated user.</summary>
sealed record BidAttempt(
    int Participant, string Key, long StartTs, long EndTs, int Status, string? Code, string? Message,
    long Sequence, decimal Price, DateTime? ServerTimestamp, bool InBurst)
{
    [JsonIgnore] public double Ms => Clock.Ms(StartTs, EndTs);
    [JsonIgnore] public bool Accepted => Status == 200 && Sequence > 0;
    [JsonIgnore] public string Outcome => Classify(Status, Code, Accepted);

    /// <summary>
    /// accepted · rejected (a rule said no, as it should) · busy (the auction's queue was full or the
    /// wait too long; certainly not placed, safe to retry) · failed (the server or the network let
    /// the bidder down). Anything not known is a failure, so nothing hides.
    /// </summary>
    public static string Classify(int status, string? code, bool accepted) =>
        accepted ? "accepted"
        : code == "AUCTION_BUSY" ? "busy"
        : code is "CONSECUTIVE_BID_NOT_ALLOWED" or "RATE_LIMITED" or "TARGET_ALREADY_REACHED"
            or "AUCTION_FINISHED" or "AUCTION_NOT_ACTIVE" ? "rejected"
        : "failed";
}

/// <summary>Shared state of one scenario's bidding phase.</summary>
sealed class BiddingContext
{
    public required Api Api { get; init; }
    public required Guid AuctionId { get; init; }
    public required string RunTag { get; init; }
    public required double Rate { get; init; }
    public required double BurstRate { get; init; }
    public required DateTime BurstFrom { get; init; }
    public required DateTime StopAt { get; init; }
    public required double MinGapMs { get; init; }
    public required int Seed { get; init; }

    public ConcurrentDictionary<long, BidInfo> Bids { get; } = new();

    private int _ended;
    public bool Ended => Volatile.Read(ref _ended) == 1;
    public string? EndedBy { get; private set; }
    public long EndedTs { get; private set; }

    public void End(string reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1) return;
        EndedBy = reason;
        EndedTs = Clock.Ts;
    }
}

/// <summary>
/// One simulated bidder: a live connection like the app's, and a bidding loop that behaves like a
/// person with the app open. It bids at random moments (a Poisson process at the given rate, faster
/// in the closing seconds), never twice in a row, and never while its last bid is still in flight —
/// the app's button is disabled in both cases.
/// </summary>
sealed class Participant
{
    private readonly object _gate = new();

    public required int Index { get; init; }
    public required TestUser User { get; init; }
    public Guid ParticipantId { get; set; }
    public HubConnection? Hub { get; private set; }

    // What arrived over the live connection, with this machine's receive time.
    public List<long> BidSequences { get; } = new(4096);
    public List<long> BidReceivedTs { get; } = new(4096);
    public List<(long Ts, decimal Top)> Leaderboards { get; } = new(512);
    public long FinishedReceivedTs { get; private set; }
    public long WinnersReceivedTs { get; private set; }
    public int Disconnects { get; private set; }
    public int Reconnects { get; private set; }

    // The price the app would show: whatever arrived last.
    public decimal Displayed { get; private set; }
    public decimal HighestSeen { get; private set; }
    public int Regressions { get; private set; }
    public double StaleMs { get; private set; }
    private long _staleSince;
    public long MaxSequenceSeen { get; private set; }
    public int OutOfOrder { get; private set; }

    // Who bid last, as this user's screen knows it.
    private Guid _lastBidder;
    private long _lastBidderSequence;

    public List<BidAttempt> Attempts { get; } = new(256);
    public int SkippedAsLastBidder { get; private set; }

    public async Task<double> ConnectAsync(Uri baseUrl, Guid auctionId, BiddingContextHolder holder)
    {
        var started = Clock.Ts;
        Hub = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUrl, "hubs/auction"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(User.Token))
            .WithAutomaticReconnect()
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            })
            .Build();

        Hub.ServerTimeout = TimeSpan.FromSeconds(60);
        Hub.On<BidBroadcast>("BidPlaced", b => OnBidPlaced(b, holder.Context));
        Hub.On<PriceChanged>("AuctionPriceChanged", p => OnPrice(p.CurrentPrice, Clock.Ts));
        Hub.On<LeaderboardMessage>("LeaderboardUpdated", m =>
        {
            var top = m.Leaderboard.Count == 0 ? 0 : m.Leaderboard.Max(e => e.HighestPrice);
            lock (_gate) Leaderboards.Add((Clock.Ts, top));
        });
        Hub.On<object>("AuctionFinished", _ =>
        {
            if (FinishedReceivedTs == 0) FinishedReceivedTs = Clock.Ts;
            holder.Context?.End("AuctionFinished broadcast");
        });
        Hub.On<object>("WinnersDeclared", _ => { if (WinnersReceivedTs == 0) WinnersReceivedTs = Clock.Ts; });
        Hub.Reconnecting += _ => { Disconnects++; return Task.CompletedTask; };
        Hub.Reconnected += async _ =>
        {
            Reconnects++;
            // As the app does after a reconnect: join the auction's group again.
            await Hub.InvokeAsync<SnapshotDto>("JoinAuction", auctionId);
        };
        Hub.Closed += _ =>
        {
            if (!_closing) Disconnects++;
            return Task.CompletedTask;
        };

        await Hub.StartAsync();
        var snapshot = await Hub.InvokeAsync<SnapshotDto>("JoinAuction", auctionId);
        lock (_gate)
        {
            Displayed = HighestSeen = snapshot.CurrentPrice;
        }
        return Clock.Ms(started, Clock.Ts);
    }

    private void OnBidPlaced(BidBroadcast bid, BiddingContext? context)
    {
        var ts = Clock.Ts;
        lock (_gate)
        {
            BidSequences.Add(bid.SequenceNumber);
            BidReceivedTs.Add(ts);
            if (bid.SequenceNumber < MaxSequenceSeen) OutOfOrder++;
            else MaxSequenceSeen = bid.SequenceNumber;
            NoteBidder(bid.SequenceNumber, bid.ParticipantId);
            ApplyPrice(bid.NewPrice, ts);
        }
        context?.Bids.TryAdd(bid.SequenceNumber, new BidInfo(bid.SequenceNumber, bid.NewPrice, bid.ServerTimestamp, bid.ParticipantId));
    }

    private void OnPrice(decimal price, long ts)
    {
        lock (_gate) ApplyPrice(price, ts);
    }

    /// <summary>
    /// The app shows the price from whichever event arrived last. If two arrive out of order, it
    /// shows an older, lower price until the next one: a "regression", and how long it lasted.
    /// </summary>
    private void ApplyPrice(decimal price, long ts)
    {
        var wasStale = Displayed < HighestSeen;
        if (price > HighestSeen) HighestSeen = price;
        Displayed = price;
        var stale = Displayed < HighestSeen;

        if (!wasStale && stale) { Regressions++; _staleSince = ts; }
        else if (wasStale && !stale) StaleMs += Clock.Ms(_staleSince, ts);
    }

    private void NoteBidder(long sequence, Guid participantId)
    {
        if (sequence <= _lastBidderSequence) return;
        _lastBidderSequence = sequence;
        _lastBidder = participantId;
    }

    public bool StillStaleAtEnd(out double ms)
    {
        lock (_gate)
        {
            ms = Displayed < HighestSeen ? Clock.Ms(_staleSince, Clock.Ts) : 0;
            return Displayed < HighestSeen;
        }
    }

    public async Task BidLoopAsync(BiddingContext context)
    {
        var random = new Random(context.Seed * 7919 + Index);

        while (!context.Ended)
        {
            var now = Clock.Now;
            if (now >= context.StopAt) break;

            var rate = now >= context.BurstFrom ? context.BurstRate : context.Rate;
            var gapMs = Math.Max(context.MinGapMs, -Math.Log(1 - random.NextDouble()) * 1000d / rate);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(gapMs, 10_000)));
            if (context.Ended) break;

            bool mine;
            lock (_gate) mine = _lastBidder == ParticipantId && ParticipantId != Guid.Empty;
            if (mine)
            {
                SkippedAsLastBidder++;
                continue;
            }

            var key = $"lt-{context.RunTag}-{Index}-{Attempts.Count}";
            var inBurst = Clock.Now >= context.BurstFrom;
            var result = await context.Api.BidAsync(User.Token, context.AuctionId, key);
            var value = result.Value;
            var attempt = new BidAttempt(
                Index, key, result.StartTs, result.EndTs, result.Status, result.Code, result.Message,
                result.Ok && value is { WasDuplicate: false } ? value.SequenceNumber : 0,
                value?.NewPrice ?? 0, value?.ServerTimestamp, inBurst);

            lock (_gate)
            {
                Attempts.Add(attempt);
                if (attempt.Accepted) NoteBidder(attempt.Sequence, ParticipantId);
            }

            if (attempt.Accepted)
            {
                context.Bids.TryAdd(attempt.Sequence,
                    new BidInfo(attempt.Sequence, attempt.Price, value!.ServerTimestamp, ParticipantId));
                if (value.ReachedTarget) context.End("target reached");
            }
            else if (result.Code is "AUCTION_FINISHED" or "TARGET_ALREADY_REACHED" or "AUCTION_NOT_ACTIVE")
            {
                context.End($"bid answered {result.Code}");
            }
        }
    }

    private volatile bool _closing;

    public async Task DisconnectAsync()
    {
        if (Hub is null) return;
        _closing = true;
        try { await Hub.DisposeAsync(); } catch { /* closing anyway */ }
    }
}

/// <summary>Lets the connection handlers, created before bidding starts, reach the bidding context.</summary>
sealed class BiddingContextHolder
{
    public BiddingContext? Context { get; set; }
}
