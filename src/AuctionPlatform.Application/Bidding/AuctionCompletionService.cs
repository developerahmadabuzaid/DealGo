using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Application.Realtime;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Application.Bidding;

/// <summary>
/// Turns a finished auction into winners, prize claims and refunds.
///
/// Every step is written to be safe under redelivery and under concurrent invocation from several
/// API instances: winner declaration is guarded by a locked flag on the auction row, and refunds
/// are guarded by UNIQUE(AuctionId, ParticipantId) on the refund table. Calling this method a
/// hundred times produces exactly one refund per losing participant.
/// </summary>
public sealed class AuctionCompletionService : IAuctionCompletionService
{
    private readonly IApplicationDbContext _db;
    private readonly IWalletService _wallets;
    private readonly IDateTimeProvider _clock;
    private readonly IAuctionRealtimeNotifier _realtime;
    private readonly INotificationService _notifications;
    private readonly IDistributedLockService _locks;
    private readonly AuctionOptions _options;
    private readonly ILogger<AuctionCompletionService> _logger;

    public AuctionCompletionService(
        IApplicationDbContext db,
        IWalletService wallets,
        IDateTimeProvider clock,
        IAuctionRealtimeNotifier realtime,
        INotificationService notifications,
        IDistributedLockService locks,
        IOptions<AuctionOptions> options,
        ILogger<AuctionCompletionService> logger)
    {
        _db = db;
        _wallets = wallets;
        _clock = clock;
        _realtime = realtime;
        _notifications = notifications;
        _locks = locks;
        _options = options.Value;
        _logger = logger;
    }

    public async Task CompleteAuctionAsync(Guid auctionId, CancellationToken ct = default)
    {
        await using var handle = await _locks.AcquireAsync(
            $"auction:complete:{auctionId}", TimeSpan.FromMinutes(2), ct);

        if (handle is null)
        {
            // Another instance is already completing this auction. Its work is authoritative.
            _logger.LogDebug("Completion for auction {AuctionId} is already running elsewhere.", auctionId);
            return;
        }

        var declared = await DeclareWinnersAsync(auctionId, ct);
        await ProcessRefundsAsync(auctionId, ct);

        if (declared is { Count: > 0 })
        {
            await BroadcastAndNotifyWinnersAsync(auctionId, declared, ct);
        }
    }

    /// <summary>
    /// Ranks participants and writes winners and prize claims in one transaction.
    /// Returns null when winners were already declared.
    /// </summary>
    private async Task<List<WinnerDto>?> DeclareWinnersAsync(Guid auctionId, CancellationToken ct)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);

        var auction = await _db.Auctions
            .FromSqlRaw("""SELECT *, xmin FROM "Auctions" WHERE "Id" = {0} FOR UPDATE""", auctionId)
            .FirstOrDefaultAsync(ct);

        if (auction is null)
        {
            throw new DomainException(ErrorCodes.AuctionNotFound, "Auction not found.");
        }

        if (auction.WinnersDeclared)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        if (auction.Status is not (AuctionStatus.Finished or AuctionStatus.Expired))
        {
            _logger.LogWarning(
                "Refusing to declare winners for auction {AuctionId} in status {Status}.",
                auctionId, auction.Status);
            await tx.CommitAsync(ct);
            return null;
        }

        var now = _clock.UtcNow;

        var participants = await _db.AuctionParticipants
            .Where(p => p.AuctionId == auctionId && p.Status != ParticipantStatus.Disqualified)
            .ToListAsync(ct);

        var ranked = RankingRules.AssignRanks(participants);
        var winners = RankingRules.SelectWinners(ranked);

        var prizes = await _db.AuctionPrizes
            .Where(p => p.AuctionId == auctionId)
            .OrderBy(p => p.Rank)
            .ToListAsync(ct);

        var winnerDtos = new List<WinnerDto>();
        var winnerParticipantIds = new HashSet<Guid>();

        for (var i = 0; i < winners.Count; i++)
        {
            var participant = winners[i];
            var rank = i + 1;
            var prize = prizes.FirstOrDefault(p => p.Rank == rank);
            if (prize is null)
            {
                _logger.LogError("Auction {AuctionId} has no prize for rank {Rank}.", auctionId, rank);
                continue;
            }

            participant.CurrentRank = rank;
            participant.Status = ParticipantStatus.Won;
            winnerParticipantIds.Add(participant.Id);

            var winner = new Winner
            {
                AuctionId = auctionId,
                ParticipantId = participant.Id,
                UserId = participant.UserId,
                Rank = rank,
                PrizeId = prize.Id,
                HighestPrice = participant.HighestPrice,
                HighestPriceAt = participant.HighestPriceAt,
                RankedAt = now
            };

            // Winning grants the right to the prize, not the prize itself: the winner still owes
            // the discounted prize price, tracked as a claim. The auction's discount applies to
            // every prize alike.
            var claim = new PrizeClaim
            {
                WinnerId = winner.Id,
                UserId = participant.UserId,
                AuctionId = auctionId,
                PrizeId = prize.Id,
                Status = PrizeClaimStatus.PaymentRequired,
                PaymentAmount = AuctionRules.PrizePaymentAmount(
                    prize.OriginalPrice, auction.CurrentPrice, auction.OriginalPrice),
                Currency = auction.Currency,
                CreatedAt = now,
                PaymentRequiredAt = now,
                PaymentDeadlineAt = now.AddDays(_options.PrizeClaimPaymentDays)
            };

            winner.PrizeClaimId = claim.Id;

            _db.Winners.Add(winner);
            _db.PrizeClaims.Add(claim);

            winnerDtos.Add(new WinnerDto(
                winner.Id, rank, participant.UserId,
                BidService.MaskName(null), participant.HighestPrice, participant.HighestPriceAt,
                prize.Id, prize.ProductNameSnapshot, prize.ProductImageSnapshot,
                prize.OriginalPrice, auction.Currency, claim.Id, claim.Status));
        }

        foreach (var p in ranked.Where(p => !winnerParticipantIds.Contains(p.Id)))
        {
            p.Status = ParticipantStatus.Lost;
        }

        auction.WinnersDeclared = true;
        auction.Status = AuctionStatus.WinnersDeclared;
        auction.FinishedAt ??= now;
        auction.UpdatedAt = now;

        // Nobody can join or bid on it now, so it leaves the front page's featured row.
        auction.IsFeatured = false;

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auctionId,
            EventType = AuctionEventType.WinnersDeclared,
            Timestamp = now,
            NewValue = $"{winnerDtos.Count} winners"
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Declared {Count} winners for auction {AuctionId}.", winnerDtos.Count, auctionId);

        // Resolve display names outside the write transaction.
        var names = await _db.Users
            .AsNoTracking()
            .Where(u => winnerDtos.Select(w => w.UserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        return winnerDtos
            .Select(w => w with { DisplayName = BidService.MaskName(names.GetValueOrDefault(w.UserId)) })
            .ToList();
    }

    /// <summary>
    /// Returns every entry fee paid into a cancelled auction, as cash in the Main wallet.
    ///
    /// A cancellation is the platform withdrawing the service, not a participant losing, so the
    /// money goes back the way it arrived rather than becoming store-only credit. The auction's
    /// own RefundDestinationWallet is deliberately ignored here: it describes how a *completed*
    /// auction pays its losers.
    ///
    /// Shares the refund table with normal completion, so UNIQUE(AuctionId, ParticipantId) still
    /// means each participant is paid exactly once however often this runs, and an auction that
    /// already refunded its losers cannot be paid a second time by being cancelled afterwards.
    /// </summary>
    public async Task RefundCancelledAuctionAsync(Guid auctionId, CancellationToken ct = default)
    {
        await using var handle = await _locks.AcquireAsync(
            $"auction:cancel-refund:{auctionId}", TimeSpan.FromMinutes(2), ct);

        if (handle is null)
        {
            _logger.LogDebug(
                "Cancellation refunds for auction {AuctionId} are already running elsewhere.", auctionId);
            return;
        }

        var auction = await _db.Auctions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == auctionId, ct);
        if (auction is null) return;

        if (auction.Status != AuctionStatus.Cancelled)
        {
            _logger.LogWarning(
                "Refusing to refund auction {AuctionId}: status is {Status}, not Cancelled.",
                auctionId, auction.Status);
            return;
        }

        // Anyone who actually paid, whatever state the cancellation caught them in.
        var candidates = await _db.AuctionParticipants
            .AsNoTracking()
            .Where(p => p.AuctionId == auctionId && p.EntryFeePaid > 0)
            .Select(p => new { p.Id, p.UserId, p.EntryFeePaid, p.Currency })
            .ToListAsync(ct);

        var alreadyRefunded = (await _db.AuctionEntryFeeRefunds
            .AsNoTracking()
            .Where(r => r.AuctionId == auctionId)
            .Select(r => r.ParticipantId)
            .ToListAsync(ct))
            .ToHashSet();

        var paid = 0;

        foreach (var candidate in candidates)
        {
            if (alreadyRefunded.Contains(candidate.Id)) continue;

            try
            {
                await RefundOneAsync(
                    auction, candidate.Id, candidate.UserId,
                    candidate.EntryFeePaid, candidate.Currency,
                    WalletType.Main, ct);

                paid++;
            }
            catch (Exception ex)
            {
                // One failure must not strand everyone else; the next run picks this participant up.
                _logger.LogError(ex,
                    "Cancellation refund failed for participant {ParticipantId} in auction {AuctionId}.",
                    candidate.Id, auctionId);
            }
        }

        var tracked = await _db.Auctions.FirstOrDefaultAsync(a => a.Id == auctionId, ct);
        if (tracked is { RefundsProcessed: false })
        {
            // Only the flag: a cancelled auction never advances to PrizeClaim.
            tracked.RefundsProcessed = true;
            tracked.UpdatedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Refunded {Count} entry fees to the Main wallet for cancelled auction {AuctionId}.",
            paid, auctionId);
    }

    /// <summary>
    /// Converts each losing participant's entry fee into wallet credit. Refunds are attempted per
    /// participant in independent transactions so one failure cannot block the rest, and the unique
    /// constraint on (AuctionId, ParticipantId) makes a repeat run a no-op.
    /// </summary>
    private async Task ProcessRefundsAsync(Guid auctionId, CancellationToken ct)
    {
        var auction = await _db.Auctions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == auctionId, ct);
        if (auction is null || !auction.WinnersDeclared) return;

        if (!auction.RefundLosingParticipantsEntryFee && !auction.RefundWinnerEntryFee)
        {
            await MarkRefundsProcessedAsync(auctionId, ct);
            return;
        }

        var candidates = await _db.AuctionParticipants
            .AsNoTracking()
            .Where(p => p.AuctionId == auctionId && p.EntryFeePaid > 0)
            .Where(p => p.Status == ParticipantStatus.Lost || p.Status == ParticipantStatus.Won)
            .Select(p => new { p.Id, p.UserId, p.EntryFeePaid, p.Currency, p.Status })
            .ToListAsync(ct);

        var alreadyRefunded = await _db.AuctionEntryFeeRefunds
            .AsNoTracking()
            .Where(r => r.AuctionId == auctionId)
            .Select(r => r.ParticipantId)
            .ToListAsync(ct);

        var refundedSet = alreadyRefunded.ToHashSet();

        foreach (var candidate in candidates)
        {
            if (refundedSet.Contains(candidate.Id)) continue;

            var eligible = candidate.Status == ParticipantStatus.Lost
                ? auction.RefundLosingParticipantsEntryFee
                : auction.RefundWinnerEntryFee;

            if (!eligible) continue;

            try
            {
                await RefundOneAsync(
                    auction, candidate.Id, candidate.UserId,
                    candidate.EntryFeePaid, candidate.Currency,
                    auction.RefundDestinationWallet, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Entry fee refund failed for participant {ParticipantId} in auction {AuctionId}.",
                    candidate.Id, auctionId);
            }
        }

        await MarkRefundsProcessedAsync(auctionId, ct);
    }

    private async Task RefundOneAsync(
        Domain.Entities.Auction auction, Guid participantId, Guid userId,
        decimal entryFee, string currency, WalletType destination, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        await using var tx = await _db.BeginTransactionAsync(ct);

        // The unique index on (AuctionId, ParticipantId) is the exactly-once guarantee. Inserting
        // the marker before moving money means a concurrent duplicate fails here, not after paying.
        var refund = new AuctionEntryFeeRefund
        {
            AuctionId = auction.Id,
            ParticipantId = participantId,
            UserId = userId,
            OriginalEntryFee = entryFee,
            RefundAmount = entryFee,
            Currency = currency,
            DestinationWallet = destination,
            Status = RefundStatus.Processing,
            AttemptCount = 1,
            CreatedAt = now
        };
        _db.AuctionEntryFeeRefunds.Add(refund);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            _logger.LogDebug(
                "Refund already recorded for participant {ParticipantId}; skipping.", participantId);
            return;
        }

        var settings = await _db.StoreSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        DateTime? expiresAt = null;
        if (destination == WalletType.Shopping &&
            settings is { ShoppingCreditExpirationEnabled: true })
        {
            expiresAt = now.AddDays(settings.ShoppingCreditExpirationDays);
        }

        var movement = new WalletMovement
        {
            UserId = userId,
            WalletType = destination,
            Amount = entryFee,
            Currency = currency,
            TransactionType = destination == WalletType.Shopping
                ? WalletTransactionType.ShoppingCredit
                : WalletTransactionType.Refund,
            ReferenceType = "Auction",
            ReferenceId = auction.Id,
            // The credit source describes shopping credit only; cash refunds leave it unset.
            CreditSource = destination == WalletType.Shopping
                ? ShoppingCreditSource.AuctionEntryFeeRefund
                : null,
            ExpiresAt = expiresAt,
            Description = $"Entry fee refund for auction {auction.AuctionNumber}",
            IdempotencyKey = $"refund:{auction.Id}:{participantId}"
        };

        var result = await _wallets.CreditAsync(movement, ct);

        var tracked = await _db.AuctionEntryFeeRefunds.FirstAsync(r => r.Id == refund.Id, ct);
        tracked.Status = RefundStatus.Completed;
        tracked.WalletTransactionId = result.TransactionId;
        tracked.ProcessedAt = now;

        var participant = await _db.AuctionParticipants.FirstAsync(p => p.Id == participantId, ct);
        if (participant.Status != ParticipantStatus.Won)
        {
            // Covers both endings: a participant who lost, and one still Active when the auction
            // was cancelled out from under them.
            participant.Status = ParticipantStatus.Refunded;
        }

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auction.Id,
            UserId = userId,
            EventType = destination == WalletType.Shopping
                ? AuctionEventType.ShoppingCreditAdded
                : AuctionEventType.EntryFeeRefunded,
            Timestamp = now,
            NewValue = entryFee.ToString("0.00")
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var isCredit = destination == WalletType.Shopping;

        await SafeNotifyAsync(async () =>
        {
            if (isCredit)
            {
                await _realtime.ShoppingCreditAddedAsync(
                    userId, entryFee, result.BalanceAfter, currency, auction.Id, ct);
            }

            // Store credit and withdrawable cash are different things to the recipient, so they
            // are never announced with the same words.
            await _notifications.NotifyAsync(new NotificationRequest(userId, new NotificationTemplate(
                isCredit ? NotificationType.ShoppingCreditAdded : NotificationType.EntryFeeRefunded,
                isCredit ? NotificationKeys.ShoppingCreditAddedTitle : NotificationKeys.EntryFeeRefundedTitle,
                isCredit ? NotificationKeys.ShoppingCreditAddedBody : NotificationKeys.EntryFeeRefundedBody,
                new object[] { $"{entryFee:0.00} {currency}" },
                new Dictionary<string, string>
                {
                    ["type"] = isCredit ? "SHOPPING_CREDIT_ADDED" : "ENTRY_FEE_REFUNDED",
                    ["auctionId"] = auction.Id.ToString(),
                    ["amount"] = entryFee.ToString("0.00")
                })), ct);
        });
    }

    private async Task MarkRefundsProcessedAsync(Guid auctionId, CancellationToken ct)
    {
        var auction = await _db.Auctions.FirstOrDefaultAsync(a => a.Id == auctionId, ct);
        if (auction is null || auction.RefundsProcessed) return;

        auction.RefundsProcessed = true;
        auction.Status = AuctionStatus.PrizeClaim;
        auction.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task BroadcastAndNotifyWinnersAsync(
        Guid auctionId, List<WinnerDto> winners, CancellationToken ct)
    {
        await SafeNotifyAsync(async () =>
        {
            await _realtime.WinnersDeclaredAsync(auctionId, winners, ct);

            foreach (var winner in winners)
            {
                await _notifications.NotifyAsync(new NotificationRequest(winner.UserId,
                    new NotificationTemplate(
                        NotificationType.YouWon,
                        NotificationKeys.YouWonTitle,
                        NotificationKeys.YouWonBody,
                        new object[] { winner.Rank, winner.PrizeName },
                        new Dictionary<string, string>
                        {
                            ["type"] = "YOU_WON",
                            ["auctionId"] = auctionId.ToString(),
                            ["rank"] = winner.Rank.ToString(),
                            ["prizeClaimId"] = winner.PrizeClaimId?.ToString() ?? string.Empty
                        })), ct);

                if (winner.PrizeClaimId is { } claimId)
                {
                    await _realtime.PrizeClaimAvailableAsync(
                        winner.UserId, claimId, auctionId, winner.PrizeValue, winner.Currency, ct);
                }
            }

            var losers = await _db.AuctionParticipants
                .AsNoTracking()
                .Where(p => p.AuctionId == auctionId &&
                            (p.Status == ParticipantStatus.Lost || p.Status == ParticipantStatus.Refunded))
                .Select(p => p.UserId)
                .ToListAsync(ct);

            if (losers.Count > 0)
            {
                await _notifications.NotifyManyAsync(losers, new NotificationTemplate(
                    NotificationType.YouLost,
                    NotificationKeys.YouLostTitle,
                    NotificationKeys.YouLostBody,
                    null,
                    new Dictionary<string, string>
                    {
                        ["type"] = "YOU_LOST",
                        ["auctionId"] = auctionId.ToString()
                    }), ct);
            }
        });
    }

    /// <summary>
    /// Applies the auction's expiration policy once its duration has elapsed without the target
    /// being reached.
    /// </summary>
    public async Task HandleExpiryAsync(Guid auctionId, CancellationToken ct = default)
    {
        await using var handle = await _locks.AcquireAsync(
            $"auction:expire:{auctionId}", TimeSpan.FromMinutes(1), ct);
        if (handle is null) return;

        var now = _clock.UtcNow;
        bool shouldComplete;
        var cancelled = false;

        await using (var tx = await _db.BeginTransactionAsync(ct))
        {
            var auction = await _db.Auctions
                .FromSqlRaw("""SELECT *, xmin FROM "Auctions" WHERE "Id" = {0} FOR UPDATE""", auctionId)
                .FirstOrDefaultAsync(ct);

            if (auction is null || !AuctionStateMachine.AcceptsBids(auction.Status))
            {
                return;
            }

            if (auction.EndsAt is not { } endsAt || now < endsAt)
            {
                return;
            }

            switch (auction.ExpirationPolicy)
            {
                case ExpirationPolicy.Extend:
                    auction.DurationSeconds += _options.ExtensionSeconds;
                    shouldComplete = false;
                    _logger.LogInformation(
                        "Auction {AuctionId} extended by {Seconds}s.", auctionId, _options.ExtensionSeconds);
                    break;

                case ExpirationPolicy.Cancel:
                    auction.Status = AuctionStatus.Cancelled;
                    auction.FinishedAt = now;
                    auction.IsFeatured = false;
                    shouldComplete = false;
                    break;

                case ExpirationPolicy.Restart:
                    auction.CurrentPrice = auction.StartingPrice;
                    auction.ActualStartedAt = now;
                    auction.FinalPhaseStartedAt = null;
                    auction.Status = AuctionStatus.Live;
                    shouldComplete = false;
                    break;

                case ExpirationPolicy.EndAndDistribute:
                default:
                    auction.Status = AuctionStatus.Finished;
                    auction.FinishedAt = now;
                    auction.IsFeatured = false;
                    shouldComplete = true;
                    break;
            }

            auction.UpdatedAt = now;

            _db.AuctionEvents.Add(new AuctionEvent
            {
                AuctionId = auctionId,
                EventType = AuctionEventType.AuctionExpired,
                Timestamp = now,
                NewValue = auction.ExpirationPolicy.ToString()
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            if (shouldComplete)
            {
                await SafeNotifyAsync(() => _realtime.AuctionFinishedAsync(
                    auctionId, auction.CurrentPrice, targetReached: false, now, ct));
            }
            else if (auction.Status == AuctionStatus.Cancelled)
            {
                cancelled = true;

                await SafeNotifyAsync(() => _realtime.AuctionCancelledAsync(
                    auctionId, "Duration expired", ct));
            }
        }

        if (shouldComplete)
        {
            await CompleteAuctionAsync(auctionId, ct);
        }
        else if (cancelled)
        {
            // The Cancel expiration policy ends the auction the same way an administrator would,
            // so it owes the participants their entry fees back in the same way.
            await RefundCancelledAuctionAsync(auctionId, ct);
        }
    }

    /// <summary>Notification and broadcast failures are logged, never propagated.</summary>
    private async Task SafeNotifyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-commit notification failed.");
        }
    }
}
