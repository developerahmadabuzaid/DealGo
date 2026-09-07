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
/// many API instances are running. In-memory locks are never used, because they would only
/// protect a single process.
/// </summary>
public sealed class BidService : IBidService
{
    private readonly IApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly IAuctionRealtimeNotifier _realtime;
    private readonly IRateLimiterService _rateLimiter;
    private readonly IAuctionCompletionService _completion;
    private readonly AuctionOptions _options;
    private readonly ILogger<BidService> _logger;

    public BidService(
        IApplicationDbContext db,
        IDateTimeProvider clock,
        IAuctionRealtimeNotifier realtime,
        IRateLimiterService rateLimiter,
        IAuctionCompletionService completion,
        IOptions<AuctionOptions> options,
        ILogger<BidService> logger)
    {
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

        var participant = await _db.AuctionParticipants
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.AuctionId == auctionId && p.UserId == userId, ct);

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

        BidOutcome outcome;
        try
        {
            outcome = await ExecuteBidTransactionAsync(auctionId, participant.Id, userId, idempotencyKey, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two identical requests raced past the fast path; the unique index settled it.
            var existing = await FindExistingBidAsync(auctionId, participant.Id, idempotencyKey, ct);
            if (existing is not null)
            {
                return await BuildResultAsync(existing, wasDuplicate: true, ct);
            }
            throw new DomainException(ErrorCodes.DuplicateRequest, "This bid was already submitted.");
        }

        // Everything below happens strictly after COMMIT. SignalR only ever reports facts that
        // are already durable in PostgreSQL.
        await PublishAsync(outcome, ct);

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

        return await BuildResultAsync(outcome.Bid, wasDuplicate: false, ct);
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
    /// The transactional core. Order of operations matters: lock, validate against the locked row,
    /// mutate, then commit.
    /// </summary>
    private async Task<BidOutcome> ExecuteBidTransactionAsync(
        Guid auctionId, Guid participantId, Guid userId, string idempotencyKey, CancellationToken ct)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);

        // SELECT ... FOR UPDATE. Concurrent bidders queue here rather than racing on price.
        var auction = await _db.Auctions
            .FromSqlRaw("""SELECT *, xmin FROM "Auctions" WHERE "Id" = {0} FOR UPDATE""", auctionId)
            .FirstOrDefaultAsync(ct);

        if (auction is null)
        {
            throw new DomainException(ErrorCodes.AuctionNotFound, "Auction not found.");
        }

        if (!AuctionStateMachine.AcceptsBids(auction.Status))
        {
            throw new DomainException(
                auction.Status is AuctionStatus.Finished or AuctionStatus.WinnersDeclared
                    or AuctionStatus.PrizeClaim or AuctionStatus.Completed
                    ? ErrorCodes.AuctionFinished
                    : ErrorCodes.AuctionNotActive,
                $"The auction is not accepting bids (status {auction.Status}).");
        }

        if (auction.TargetReached)
        {
            throw new DomainException(ErrorCodes.TargetAlreadyReached, "The target price has already been reached.");
        }

        var now = _clock.UtcNow;

        if (auction.EndsAt is { } endsAt && now >= endsAt)
        {
            // The duration elapsed. Reject and let the expiry worker apply the configured policy.
            throw new DomainException(ErrorCodes.AuctionFinished, "The auction duration has elapsed.");
        }

        var participant = await _db.AuctionParticipants
            .FirstOrDefaultAsync(p => p.Id == participantId, ct)
            ?? throw new DomainException(ErrorCodes.ParticipantNotFound, "Participant not found.");

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

        _logger.LogInformation(
            "Bid {Sequence} on auction {AuctionId} by {UserId}: {Previous} -> {New}{Target}",
            sequenceNumber, auctionId, userId, previousPrice, newPrice,
            reachedTarget ? " (TARGET REACHED)" : string.Empty);

        return new BidOutcome(
            bid,
            auction.TargetPrice,
            auction.StartingPrice,
            auction.Status,
            enteredFinalPhase,
            reachedTarget,
            auction.FinalPhasePrice,
            auction.RemainingSeconds,
            now);
    }

    private async Task PublishAsync(BidOutcome outcome, CancellationToken ct)
    {
        try
        {
            var bid = outcome.Bid;
            var progress = AuctionRules.Progress(outcome.StartingPrice, bid.NewPrice, outcome.TargetPrice);
            var displayName = await GetDisplayNameAsync(bid.UserId, ct);

            await _realtime.BidPlacedAsync(bid.AuctionId, new BidBroadcast(
                bid.Id, bid.SequenceNumber, bid.ParticipantId, displayName,
                bid.PreviousPrice, bid.NewPrice, outcome.TargetPrice, progress,
                bid.ReachedTarget, bid.ServerTimestamp), ct);

            await _realtime.AuctionPriceChangedAsync(
                bid.AuctionId, bid.NewPrice, outcome.TargetPrice, progress, ct);

            if (outcome.EnteredFinalPhase)
            {
                await _realtime.FinalPhaseStartedAsync(
                    bid.AuctionId, bid.NewPrice, outcome.TargetPrice, outcome.RemainingSeconds, ct);
            }

            if (outcome.AuctionFinished)
            {
                await _realtime.AuctionFinishedAsync(
                    bid.AuctionId, bid.NewPrice, targetReached: true, outcome.Now, ct);
            }

            var leaderboard = await GetLeaderboardAsync(bid.AuctionId, null, 10, ct);
            await _realtime.LeaderboardUpdatedAsync(bid.AuctionId, leaderboard, ct);
        }
        catch (Exception ex)
        {
            // Broadcasting is best-effort: the bid is already committed and clients resynchronise
            // from the snapshot endpoint on reconnect.
            _logger.LogWarning(ex, "Failed to broadcast bid for auction {AuctionId}", outcome.Bid.AuctionId);
        }
    }

    private async Task<string> GetDisplayNameAsync(Guid userId, CancellationToken ct)
    {
        var name = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync(ct);

        return MaskName(name);
    }

    /// <summary>Leaderboards are public, so names are partially masked.</summary>
    internal static string MaskName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "Participant";
        var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = parts[0];
        if (parts.Length == 1)
        {
            return first.Length <= 2 ? first : first[..2] + new string('*', Math.Min(3, first.Length - 2));
        }
        return $"{first} {parts[^1][0]}.";
    }

    private async Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(
        Guid auctionId, Guid? meUserId, int take, CancellationToken ct)
    {
        var rows = await _db.AuctionParticipants
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
            i + 1, r.Id, r.UserId, MaskName(r.Name), r.HighestPrice,
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
        DateTime Now);
}
