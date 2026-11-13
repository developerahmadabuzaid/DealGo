using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Application.Realtime;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Application.Prizes;

public interface IPrizeClaimExpiryService
{
    /// <summary>
    /// Closes every prize claim whose payment time has run out unpaid, and returns the winner's
    /// entry fee to their Shopping wallet. Returns how many claims it closed.
    /// </summary>
    Task<int> ExpireOverdueAsync(CancellationToken ct = default);
}

/// <summary>
/// A winner has <see cref="AuctionOptions.PrizeClaimPaymentHours"/> (24) hours to pay for their
/// device. When that passes unpaid, the claim expires: the device is no longer theirs to buy, and
/// the entry fee they paid to join comes back as shopping credit, as it does for the participants
/// who did not win.
///
/// Exactly once: the claim is closed under its row lock, so a payment arriving at the same moment
/// either goes through first (and nothing expires) or is refused; and the refund marker's unique
/// index on (auction, participant) stops a second credit, including for an auction that already
/// refunded its winners when it closed.
/// </summary>
public sealed class PrizeClaimExpiryService : IPrizeClaimExpiryService
{
    private readonly IApplicationDbContext _db;
    private readonly IWalletService _wallets;
    private readonly INotificationService _notifications;
    private readonly IAuctionRealtimeNotifier _realtime;
    private readonly IDateTimeProvider _clock;
    private readonly AuctionOptions _options;
    private readonly ILogger<PrizeClaimExpiryService> _logger;

    public PrizeClaimExpiryService(
        IApplicationDbContext db,
        IWalletService wallets,
        INotificationService notifications,
        IAuctionRealtimeNotifier realtime,
        IDateTimeProvider clock,
        IOptions<AuctionOptions> options,
        ILogger<PrizeClaimExpiryService> logger)
    {
        _db = db;
        _wallets = wallets;
        _notifications = notifications;
        _realtime = realtime;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> ExpireOverdueAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var appliesFrom = _options.PrizeClaimExpiryAppliesFrom;

        var overdue = await _db.PrizeClaims.AsNoTracking()
            .Where(c => (c.Status == PrizeClaimStatus.Pending || c.Status == PrizeClaimStatus.PaymentRequired)
                        && c.PaymentDeadlineAt != null && c.PaymentDeadlineAt <= now
                        && c.CreatedAt >= appliesFrom)
            .OrderBy(c => c.PaymentDeadlineAt)
            .Select(c => c.Id)
            .Take(50)
            .ToListAsync(ct);

        var expired = 0;
        foreach (var id in overdue)
        {
            try
            {
                if (await ExpireOneAsync(id, ct)) expired++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Left as it was; the next pass tries again.
                _logger.LogError(ex, "Prize claim {ClaimId} could not be expired.", id);
                if (_db is DbContext context) context.ChangeTracker.Clear();
            }
        }

        return expired;
    }

    private async Task<bool> ExpireOneAsync(Guid claimId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await using var tx = await _db.BeginTransactionAsync(ct);

        var claim = await _db.PrizeClaims
            .FromSqlRaw("""SELECT *, xmin FROM "PrizeClaims" WHERE "Id" = {0} FOR UPDATE""", claimId)
            .FirstOrDefaultAsync(ct);

        // Paid in the meantime, or no longer overdue: nothing to do.
        if (claim is null
            || claim.Status is not (PrizeClaimStatus.Pending or PrizeClaimStatus.PaymentRequired)
            || claim.PaymentDeadlineAt is not { } deadline || deadline > now)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        claim.Status = PrizeClaimStatus.Expired;
        claim.ExpiredAt = now;

        var winner = await _db.Winners.AsNoTracking().FirstAsync(w => w.Id == claim.WinnerId, ct);
        var participant = await _db.AuctionParticipants.AsNoTracking().FirstAsync(p => p.Id == winner.ParticipantId, ct);
        var auction = await _db.Auctions.AsNoTracking().FirstAsync(a => a.Id == claim.AuctionId, ct);

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auction.Id,
            UserId = claim.UserId,
            EventType = AuctionEventType.PrizeClaimExpired,
            Timestamp = now,
            NewValue = claim.Id.ToString()
        });

        // Already refunded when the auction closed (an auction may refund its winners too), or no
        // fee was paid: the claim still expires, and no money moves.
        var alreadyRefunded = await _db.AuctionEntryFeeRefunds
            .AnyAsync(r => r.AuctionId == auction.Id && r.ParticipantId == participant.Id, ct);
        var fee = participant.EntryFeePaid;
        WalletOperationResult? credit = null;

        if (!alreadyRefunded && fee > 0)
        {
            var refund = new AuctionEntryFeeRefund
            {
                AuctionId = auction.Id,
                ParticipantId = participant.Id,
                UserId = claim.UserId,
                OriginalEntryFee = fee,
                RefundAmount = fee,
                Currency = participant.Currency,
                DestinationWallet = WalletType.Shopping,
                Status = RefundStatus.Processing,
                AttemptCount = 1,
                CreatedAt = now
            };
            _db.AuctionEntryFeeRefunds.Add(refund);
            await _db.SaveChangesAsync(ct);

            var settings = await _db.StoreSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            credit = await _wallets.CreditAsync(new WalletMovement
            {
                UserId = claim.UserId,
                WalletType = WalletType.Shopping,
                Amount = fee,
                Currency = participant.Currency,
                TransactionType = WalletTransactionType.ShoppingCredit,
                ReferenceType = "Auction",
                ReferenceId = auction.Id,
                CreditSource = ShoppingCreditSource.AuctionEntryFeeRefund,
                ExpiresAt = settings is { ShoppingCreditExpirationEnabled: true }
                    ? now.AddDays(settings.ShoppingCreditExpirationDays)
                    : null,
                Description = $"Entry fee returned: prize for auction {auction.AuctionNumber} not paid in time",
                IdempotencyKey = $"refund:{auction.Id}:{participant.Id}"
            }, ct);

            refund.Status = RefundStatus.Completed;
            refund.WalletTransactionId = credit.TransactionId;
            refund.ProcessedAt = now;

            _db.AuctionEvents.Add(new AuctionEvent
            {
                AuctionId = auction.Id,
                UserId = claim.UserId,
                EventType = AuctionEventType.ShoppingCreditAdded,
                Timestamp = now,
                NewValue = fee.ToString("0.00")
            });
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Prize claim {ClaimId} expired unpaid; entry fee {Fee} {Currency} {Outcome}.",
            claim.Id, fee, participant.Currency, credit is null ? "not returned (already refunded or none)" : "returned as shopping credit");

        // After the commit: telling the person never decides whether the money moved.
        try
        {
            if (credit is not null)
            {
                await _realtime.ShoppingCreditAddedAsync(
                    claim.UserId, fee, credit.BalanceAfter, participant.Currency, auction.Id, ct);
            }

            await _notifications.NotifyAsync(new NotificationRequest(claim.UserId, new NotificationTemplate(
                NotificationType.PrizeClaimExpired,
                NotificationKeys.PrizeClaimExpiredTitle,
                NotificationKeys.PrizeClaimExpiredBody,
                new object[] { $"{fee:0.00} {participant.Currency}", auction.Title },
                new Dictionary<string, string>
                {
                    ["type"] = "PRIZE_CLAIM_EXPIRED",
                    ["auctionId"] = auction.Id.ToString(),
                    ["prizeClaimId"] = claim.Id.ToString(),
                    ["amount"] = fee.ToString("0.00")
                })), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prize claim {ClaimId} expired, but the winner could not be told.", claim.Id);
        }

        return true;
    }
}
