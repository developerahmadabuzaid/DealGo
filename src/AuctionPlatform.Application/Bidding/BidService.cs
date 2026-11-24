using System.Diagnostics;
using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Realtime;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Application.Bidding;

/// <summary>
/// The bid engine.
///
/// Correctness rests on one thing: every bid for an auction takes a row-level lock on that
/// auction inside a database transaction, so bids on the same auction serialise no matter how
/// many API instances are running.
///
/// Who goes next is decided earlier, in memory, by <see cref="IAuctionQueue"/>: a bid waits for its
/// auction's turn there, holding no database connection, instead of waiting on the row lock with a
/// connection taken from everyone else. The queue only protects one process, so it is never the
/// guarantee — the database lock is.
/// </summary>
public sealed class BidService : IBidService
{
    private readonly IApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly IAuctionRealtimeNotifier _realtime;
    private readonly IRateLimiterService _rateLimiter;
    private readonly IAuctionCompletionService _completion;
    private readonly ILeaderboardBroadcaster _leaderboard;
    private readonly IAuctionQueue _queue;
    private readonly AuctionOptions _options;
    private readonly ILogger<BidService> _logger;

    public BidService(
        IApplicationDbContext db,
        IDateTimeProvider clock,
        IAuctionRealtimeNotifier realtime,
        IRateLimiterService rateLimiter,
        IAuctionCompletionService completion,
        ILeaderboardBroadcaster leaderboard,
        IAuctionQueue queue,
        IOptions<AuctionOptions> options,
        ILogger<BidService> logger)
    {
        _leaderboard = leaderboard;
        _queue = queue;
        _db = db;
        _clock = clock;
        _realtime = realtime;
        _rateLimiter = rateLimiter;
        _completion = completion;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<BidResultDto> PlaceBidAsync(
        Guid auctionId, Guid userId, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "An idempotency key is required.");
        }

        // The bidder's name comes with the participant row: it is what the others are shown, and
        // asking for it separately was one more round trip on every bid.
        var row = await _db.AuctionParticipants
            .AsNoTracking()
            .Where(p => p.AuctionId == auctionId && p.UserId == userId)
            .Select(p => new { Participant = p, Name = p.User != null ? p.User.FullName : null })
            .FirstOrDefaultAsync(ct);
        var participant = row?.Participant;

        if (participant is null)
        {
            throw new DomainException(
                ErrorCodes.ParticipantNotFound,
                "You have not joined this auction.");
        }

        if (participant.Status == ParticipantStatus.PendingPayment)
        {
            throw new DomainException(
                ErrorCodes.EntryPaymentRequired,
                "Your entry fee payment has not completed.");
        }

        // Fast path: a retry of a bid we already accepted returns the original outcome.
        var replay = await FindExistingBidAsync(auctionId, participant.Id, idempotencyKey, ct);
        if (replay is not null)
        {
            return await BuildResultAsync(replay, wasDuplicate: true, ct);
        }

        await EnforceRateLimitAsync(auctionId, userId, ct);

        // A late tap on an auction that can never take another bid is answered here, without
        // queueing and without a transaction.
        RefuseIfClosed(auctionId);

        BidOutcome? outcome = null;
        var duplicate = false;
        DomainException? refusedAsConsecutive = null;

        // Wait for this auction's turn in memory, holding no database connection. Leaving before it
        // comes — the bidder gave up — means the bid is never placed.
        using (await _queue.EnterAsync(auctionId, "bid", ct))
        {
            // It may have closed while this bid was waiting.
            RefuseIfClosed(auctionId);

            try
            {
                outcome = await ExecuteBidAsync(auctionId, participant, userId, idempotencyKey, ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                duplicate = true;
            }
            catch (DomainException ex) when (ex.Code == ErrorCodes.ConsecutiveBidNotAllowed)
            {
                // A retry of this person's own last bid looks like a second bid in a row. Whether it
                // is the same request is settled below, outside the turn.
                refusedAsConsecutive = ex;
            }
        }

        if (duplicate || refusedAsConsecutive is not null)
        {
            // The same request sent twice gets the same answer twice: the original bid, never a
            // second one. The unique index on the idempotency key is what guarantees it.
            var existing = await FindExistingBidAsync(auctionId, participant.Id, idempotencyKey, ct);
            if (existing is not null)
            {
                return await BuildResultAsync(existing, wasDuplicate: true, ct);
            }
            throw refusedAsConsecutive ?? new DomainException(ErrorCodes.DuplicateRequest, "This bid was already submitted.");
        }

        var placed = outcome!.Bid;
        _logger.LogInformation(
            "Bid {Sequence} on auction {AuctionId} by {UserId}: {Previous} -> {New}{Target}",
            placed.SequenceNumber, auctionId, userId, placed.PreviousPrice, placed.NewPrice,
            placed.ReachedTarget ? " (TARGET REACHED)" : string.Empty);

        // Strictly after COMMIT — SignalR only ever reports facts already durable in PostgreSQL — and
        // after passing the turn on. Sending to hundreds of phones inside the turn made every bid
        // wait for the one before it to finish sending (load test: about 40% fewer bids a second at
        // 400 participants). Each update carries the bid's number, and the app drops any that
        // arrives after a newer one, so order on the wire no longer matters.
        await AnnounceAsync(outcome, DisplayName(row!.Name));

        if (outcome.AuctionFinished)
        {
            try
            {
                await _completion.CompleteAuctionAsync(auctionId, ct);
            }
            catch (Exception ex)
            {
                // The bid itself is already durable. Reporting a completion failure back to the
                // bidder would wrongly suggest their bid was rejected, so it is logged and left to
                // the completion worker, which retries every 30 seconds.
                _logger.LogError(ex,
                    "Auction {AuctionId} finished but completion failed; the worker will retry.",
                    auctionId);
            }
        }

        // Built from what the transaction already holds: reading the auction and the participant
        // back again only made the bidder wait longer for the button.
        var bid = outcome.Bid;
        return new BidResultDto(
            bid.Id,
            bid.AuctionId,
            bid.SequenceNumber,
            bid.PreviousPrice,
            bid.NewPrice,
            outcome.TargetPrice,
            bid.ReachedTarget,
            outcome.Status == AuctionStatus.FinalPhase || bid.NewPrice >= outcome.FinalPhasePrice,
            outcome.Status,
            await ComputeRankAsync(auctionId, outcome.Participant, ct),
            outcome.Participant.HighestPrice,
            bid.ServerTimestamp,
            false);
    }

    private void RefuseIfClosed(Guid auctionId)
    {
        if (_queue.RefusalFor(auctionId) is not { } refusal) return;
        AppMetrics.BidsRefusedClosed.Add(1);
        throw refusal;
    }

    private async Task EnforceRateLimitAsync(Guid auctionId, Guid userId, CancellationToken ct)
    {
        var perSecond = await _rateLimiter.TryAcquireAsync(
            $"bid:{auctionId}:{userId}:s", _options.BidRateLimitPerSecond, TimeSpan.FromSeconds(1), ct);

        var perMinute = perSecond && await _rateLimiter.TryAcquireAsync(
            $"bid:{auctionId}:{userId}:m", _options.BidRateLimitPerMinute, TimeSpan.FromMinutes(1), ct);

        if (!perSecond || !perMinute)
        {
            throw new DomainException(ErrorCodes.RateLimited, "Too many bids. Please slow down.");
        }
    }

    /// <summary>
    /// The bid transaction, with the participant as read before the turn. If that row changed in
    /// the meantime — only another bid by the same person, from another device, can change it — the
    /// update matches no row (its xmin moved), and the bid is tried once more, reading the
    /// participant under the lock this time. Nothing is written by the failed attempt.
    /// </summary>
    private async Task<BidOutcome> ExecuteBidAsync(
        Guid auctionId, AuctionParticipant participant, Guid userId, string idempotencyKey, CancellationToken ct)
    {
        try
        {
            return await ExecuteBidTransactionAsync(auctionId, participant.Id, participant, userId, idempotencyKey, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ClearChangeTracker();
            return await ExecuteBidTransactionAsync(auctionId, participant.Id, null, userId, idempotencyKey, ct);
        }
    }

    /// <summary>
    /// The transactional core. Order of operations matters: lock, validate against the locked row,
    /// mutate, then commit.
    ///
    /// Every other bid on the auction waits while this runs, so it makes as few trips to the
    /// database as it can: the locking read (which also carries who bid last), one batch with every
    /// write, and the commit. The participant comes in already read — checked by its row version
    /// when written — instead of being read again under the lock.
    /// </summary>
    private async Task<BidOutcome> ExecuteBidTransactionAsync(
        Guid auctionId, Guid participantId, AuctionParticipant? readBefore, Guid userId, string idempotencyKey,
        CancellationToken ct)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);

        // SELECT ... FOR UPDATE. Concurrent bidders queue here rather than racing on price.
        var lockRequested = Stopwatch.GetTimestamp();
        var auction = await _db.Auctions
            .FromSqlRaw("""SELECT *, xmin FROM "Auctions" WHERE "Id" = {0} FOR UPDATE""", auctionId)
            .FirstOrDefaultAsync(ct);
        var lockAcquired = Stopwatch.GetTimestamp();
        AppMetrics.RecordLockWait("bid", Stopwatch.GetElapsedTime(lockRequested, lockAcquired));

        if (auction is null)
        {
            throw new DomainException(ErrorCodes.AuctionNotFound, "Auction not found.");
        }

        if (!AuctionStateMachine.AcceptsBids(auction.Status))
        {
            var finished = auction.Status is AuctionStatus.Finished or AuctionStatus.WinnersDeclared
                or AuctionStatus.PrizeClaim or AuctionStatus.Completed;
            var code = finished ? ErrorCodes.AuctionFinished : ErrorCodes.AuctionNotActive;
            var message = $"The auction is not accepting bids (status {auction.Status}).";

            // Finished and cancelled are for good; paused or not started yet are not.
            if (finished || auction.Status == AuctionStatus.Cancelled) _queue.MarkClosed(auctionId, code, message);
            throw new DomainException(code, message);
        }

        if (auction.TargetReached)
        {
            _queue.MarkClosed(auctionId, ErrorCodes.TargetAlreadyReached, "The target price has already been reached.");
            throw new DomainException(ErrorCodes.TargetAlreadyReached, "The target price has already been reached.");
        }

        var now = _clock.UtcNow;

        if (auction.EndsAt is { } endsAt && now >= endsAt)
        {
            // The duration elapsed. Reject and let the expiry worker apply the configured policy.
            // Unless that policy extends or restarts the auction, it can never take a bid again.
            if (auction.ExpirationPolicy is ExpirationPolicy.EndAndDistribute or ExpirationPolicy.Cancel)
                _queue.MarkClosed(auctionId, ErrorCodes.AuctionFinished, "The auction duration has elapsed.");
            throw new DomainException(ErrorCodes.AuctionFinished, "The auction duration has elapsed.");
        }

        // No one bids twice in a row: after a bid, someone else has to bid before the same
        // participant may bid again. Read from the locked row itself, so two quick taps from one
        // person queue behind each other and the second sees the first.
        var lastBidder = auction.LastBidParticipantId;
        if (lastBidder is null && auction.LastSequenceNumber > 0)
        {
            // Bids placed before the auction row kept its last bidder.
            lastBidder = await _db.Bids
                .Where(b => b.AuctionId == auction.Id && b.SequenceNumber == auction.LastSequenceNumber)
                .Select(b => (Guid?)b.ParticipantId)
                .FirstOrDefaultAsync(ct);
        }

        if (lastBidder == participantId)
        {
            throw new DomainException(
                ErrorCodes.ConsecutiveBidNotAllowed,
                "You placed the last bid. Wait for another participant to bid first.");
        }

        AuctionParticipant participant;
        if (readBefore is not null)
        {
            // Written back only if unchanged since it was read: its xmin is part of the UPDATE.
            _db.AuctionParticipants.Attach(readBefore);
            participant = readBefore;
        }
        else
        {
            participant = await _db.AuctionParticipants.FirstOrDefaultAsync(p => p.Id == participantId, ct)
                ?? throw new DomainException(ErrorCodes.ParticipantNotFound, "Participant not found.");
        }

        var previousPrice = auction.CurrentPrice;
        var newPrice = AuctionRules.NextPrice(previousPrice, auction.BidIncrement, auction.TargetPrice);
        var reachedTarget = AuctionRules.ReachesTarget(newPrice, auction.TargetPrice);

        var sequenceNumber = auction.LastSequenceNumber + 1;

        var bid = new Bid
        {
            AuctionId = auction.Id,
            ParticipantId = participant.Id,
            UserId = userId,
            SequenceNumber = sequenceNumber,
            PreviousPrice = previousPrice,
            NewPrice = newPrice,
            IdempotencyKey = idempotencyKey,
            ReachedTarget = reachedTarget,
            CreatedAt = now,
            ServerTimestamp = now
        };
        _db.Bids.Add(bid);

        auction.CurrentPrice = newPrice;
        auction.LastSequenceNumber = sequenceNumber;
        auction.LastBidParticipantId = participant.Id;
        auction.UpdatedAt = now;

        var enteredFinalPhase = false;
        if (auction.Status == AuctionStatus.Live &&
            AuctionRules.EntersFinalPhase(newPrice, auction.FinalPhasePrice))
        {
            auction.Status = AuctionStatus.FinalPhase;
            auction.FinalPhaseStartedAt = now;
            enteredFinalPhase = true;
        }

        if (reachedTarget)
        {
            auction.TargetReached = true;
            auction.Status = AuctionStatus.Finished;
            auction.FinishedAt = now;
        }

        // The participant's highest price and the instant they first reached it are the ranking
        // keys, so both are recorded from the server clock inside this transaction.
        participant.CurrentPrice = newPrice;
        participant.BidCount += 1;
        participant.LastBidAt = now;

        if (newPrice > participant.HighestPrice)
        {
            participant.HighestPrice = newPrice;
            participant.HighestPriceAt = now;
        }

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auction.Id,
            UserId = userId,
            EventType = AuctionEventType.BidPlaced,
            Timestamp = now,
            OldValue = previousPrice.ToString("0.00"),
            NewValue = newPrice.ToString("0.00"),
            Metadata = $"{{\"sequenceNumber\":{sequenceNumber}}}"
        });

        if (enteredFinalPhase)
        {
            _db.AuctionEvents.Add(new AuctionEvent
            {
                AuctionId = auction.Id,
                EventType = AuctionEventType.FinalPhaseStarted,
                Timestamp = now,
                NewValue = newPrice.ToString("0.00")
            });
        }

        if (reachedTarget)
        {
            _db.AuctionEvents.Add(new AuctionEvent
            {
                AuctionId = auction.Id,
                UserId = userId,
                EventType = AuctionEventType.TargetReached,
                Timestamp = now,
                NewValue = newPrice.ToString("0.00")
            });
            _db.AuctionEvents.Add(new AuctionEvent
            {
                AuctionId = auction.Id,
                EventType = AuctionEventType.AuctionFinished,
                Timestamp = now,
                NewValue = auction.Status.ToString()
            });
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        AppMetrics.BidLockHeld.Record(Stopwatch.GetElapsedTime(lockAcquired).TotalSeconds);

        if (reachedTarget)
        {
            // What any later bid would now be told by the database, given without asking it.
            _queue.MarkClosed(auctionId, ErrorCodes.AuctionFinished,
                $"The auction is not accepting bids (status {AuctionStatus.Finished}).");
        }

        // Logged by the caller, after the turn is passed on: writing the log is not the next
        // bidder's business.

        return new BidOutcome(
            bid,
            auction.TargetPrice,
            auction.StartingPrice,
            auction.Status,
            enteredFinalPhase,
            reachedTarget,
            auction.FinalPhasePrice,
            auction.RemainingSeconds,
            now,
            participant);
    }

    /// <summary>
    /// Sends the bid to everyone watching. Each update is numbered by the bid, so a phone that
    /// receives two out of order keeps the newer.
    ///
    /// The bidder's cancellation is deliberately not passed on: closing the app straight after a
    /// bid must not cancel its announcement to everyone else.
    /// </summary>
    private Task AnnounceAsync(BidOutcome outcome, string displayName)
    {
        var bid = outcome.Bid;
        var sends = new List<Task>(4);
        try
        {
            var progress = AuctionRules.Progress(outcome.StartingPrice, bid.NewPrice, outcome.TargetPrice);

            sends.Add(_realtime.BidPlacedAsync(bid.AuctionId, new BidBroadcast(
                bid.Id, bid.SequenceNumber, bid.ParticipantId, displayName,
                bid.PreviousPrice, bid.NewPrice, outcome.TargetPrice, progress,
                bid.ReachedTarget, bid.ServerTimestamp)));

            sends.Add(_realtime.AuctionPriceChangedAsync(
                bid.AuctionId, bid.NewPrice, outcome.TargetPrice, progress, bid.SequenceNumber));

            if (outcome.EnteredFinalPhase)
            {
                sends.Add(_realtime.FinalPhaseStartedAsync(
                    bid.AuctionId, bid.NewPrice, outcome.TargetPrice, outcome.RemainingSeconds));
            }

            if (outcome.AuctionFinished)
            {
                sends.Add(_realtime.AuctionFinishedAsync(
                    bid.AuctionId, bid.NewPrice, targetReached: true, outcome.Now));
            }

            // Sent a few times a second by the broadcaster rather than queried here on every bid,
            // which kept each bidder waiting on one more database round trip.
            _leaderboard.Request(bid.AuctionId);
        }
        catch (Exception ex)
        {
            // Broadcasting is best-effort: the bid is already committed and clients resynchronise
            // from the snapshot endpoint on reconnect.
            _logger.LogWarning(ex, "Failed to broadcast bid for auction {AuctionId}", bid.AuctionId);
        }

        return FinishAnnouncingAsync(sends, bid.AuctionId);
    }

    private async Task FinishAnnouncingAsync(List<Task> sends, Guid auctionId)
    {
        try
        {
            await Task.WhenAll(sends);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast bid for auction {AuctionId}", auctionId);
        }
    }

    /// <summary>
    /// The name other participants see: the person's full name, as they entered it. It used to be
    /// cut to "Ahmad A."; the business wants the whole name shown in auctions.
    /// </summary>
    internal static string DisplayName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "Participant";
        return string.Join(' ', fullName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The top bidders, as the live leaderboard shows them.</summary>
    internal static async Task<IReadOnlyList<LeaderboardEntryDto>> ReadLeaderboardAsync(
        IApplicationDbContext db, Guid auctionId, int take, CancellationToken ct)
    {
        Guid? meUserId = null;
        var rows = await db.AuctionParticipants
            .AsNoTracking()
            .Where(p => p.AuctionId == auctionId && p.BidCount > 0)
            .OrderByDescending(p => p.HighestPrice)
            .ThenBy(p => p.HighestPriceAt)
            .Take(take)
            .Select(p => new
            {
                p.Id, p.UserId, p.HighestPrice, p.HighestPriceAt, p.BidCount,
                Name = p.User != null ? p.User.FullName : null
            })
            .ToListAsync(ct);

        return rows.Select((r, i) => new LeaderboardEntryDto(
            i + 1, r.Id, r.UserId, DisplayName(r.Name), r.HighestPrice,
            r.HighestPriceAt, r.BidCount, meUserId.HasValue && r.UserId == meUserId.Value)).ToList();
    }

    private async Task<Bid?> FindExistingBidAsync(
        Guid auctionId, Guid participantId, string idempotencyKey, CancellationToken ct) =>
        await _db.Bids
            .AsNoTracking()
            .FirstOrDefaultAsync(b =>
                b.AuctionId == auctionId &&
                b.ParticipantId == participantId &&
                b.IdempotencyKey == idempotencyKey, ct);

    private async Task<BidResultDto> BuildResultAsync(Bid bid, bool wasDuplicate, CancellationToken ct)
    {
        var auction = await _db.Auctions
            .AsNoTracking()
            .FirstAsync(a => a.Id == bid.AuctionId, ct);

        var participant = await _db.AuctionParticipants
            .AsNoTracking()
            .FirstAsync(p => p.Id == bid.ParticipantId, ct);

        var rank = await ComputeRankAsync(bid.AuctionId, participant, ct);

        return new BidResultDto(
            bid.Id,
            bid.AuctionId,
            bid.SequenceNumber,
            bid.PreviousPrice,
            bid.NewPrice,
            auction.TargetPrice,
            bid.ReachedTarget,
            auction.Status == AuctionStatus.FinalPhase || auction.CurrentPrice >= auction.FinalPhasePrice,
            auction.Status,
            rank,
            participant.HighestPrice,
            bid.ServerTimestamp,
            wasDuplicate);
    }

    /// <summary>
    /// Live rank without materialising every participant: count how many did strictly better,
    /// applying the same highest-price-then-earliest-timestamp ordering used for final ranking.
    /// </summary>
    private async Task<int> ComputeRankAsync(Guid auctionId, AuctionParticipant me, CancellationToken ct)
    {
        if (me.BidCount == 0) return 0;

        var better = await _db.AuctionParticipants
            .AsNoTracking()
            .Where(p => p.AuctionId == auctionId && p.BidCount > 0 && p.Id != me.Id)
            .CountAsync(p =>
                p.HighestPrice > me.HighestPrice ||
                (p.HighestPrice == me.HighestPrice &&
                 p.HighestPriceAt != null && me.HighestPriceAt != null &&
                 p.HighestPriceAt < me.HighestPriceAt), ct);

        return better + 1;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.GetType().Name == "PostgresException" &&
        (ex.InnerException.Data["SqlState"] as string) == "23505";

    private sealed record BidOutcome(
        Bid Bid,
        decimal TargetPrice,
        decimal StartingPrice,
        AuctionStatus Status,
        bool EnteredFinalPhase,
        bool AuctionFinished,
        decimal FinalPhasePrice,
        int? RemainingSeconds,
        DateTime Now,
        AuctionParticipant Participant);
}
