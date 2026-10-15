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

namespace AuctionPlatform.Application.Auctions;

/// <summary>
/// Registration. Paying the entry fee, creating the participant, and — when the final required
/// participant arrives — scheduling the start.
/// </summary>
public sealed class ParticipationService : IParticipationService
{
    private readonly IApplicationDbContext _db;
    private readonly IWalletService _wallets;
    private readonly IDateTimeProvider _clock;
    private readonly IAuctionRealtimeNotifier _realtime;
    private readonly INotificationService _notifications;
    private readonly IAuditService _audit;
    private readonly ILogger<ParticipationService> _logger;

    public ParticipationService(
        IApplicationDbContext db,
        IWalletService wallets,
        IDateTimeProvider clock,
        IAuctionRealtimeNotifier realtime,
        INotificationService notifications,
        IAuditService audit,
        ILogger<ParticipationService> logger)
    {
        _db = db;
        _wallets = wallets;
        _clock = clock;
        _realtime = realtime;
        _notifications = notifications;
        _audit = audit;
        _logger = logger;
    }

    public async Task<MyParticipationDto> JoinAsync(
        Guid auctionId, Guid userId, string? idempotencyKey, CancellationToken ct = default)
    {
        var existing = await GetMyParticipationAsync(auctionId, userId, ct);
        if (existing is not null)
        {
            throw new DomainException(ErrorCodes.AlreadyJoined, "You have already joined this auction.");
        }

        var now = _clock.UtcNow;
        JoinOutcome outcome;

        try
        {
            outcome = await ExecuteJoinAsync(auctionId, userId, idempotencyKey, now, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new DomainException(ErrorCodes.AlreadyJoined, "You have already joined this auction.");
        }

        await PublishJoinAsync(outcome, ct);

        return outcome.Participation;
    }

    public async Task<MyParticipationDto> AddByAdminAsync(
        Guid auctionId, AddParticipantRequest request, CancellationToken ct = default)
    {
        if (request.EntryFee < 0)
        {
            throw new DomainException(ErrorCodes.InvalidAmount, "The entry fee cannot be negative.");
        }

        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == request.UserId)
            .Select(u => new { u.Id, u.IsActive })
            .FirstOrDefaultAsync(ct)
            ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");

        if (!user.IsActive)
        {
            throw new DomainException(ErrorCodes.AccountDisabled, "This account is disabled.");
        }

        var existing = await GetMyParticipationAsync(auctionId, user.Id, ct);
        if (existing is not null)
        {
            throw new DomainException(ErrorCodes.AlreadyJoined, "This user has already joined this auction.");
        }

        var now = _clock.UtcNow;
        var fee = Money.Round(request.EntryFee);
        JoinOutcome outcome;

        try
        {
            // The same path a user takes to join, so the counter, the start schedule and the wallet
            // charge behave identically. Only the fee differs.
            outcome = await ExecuteJoinAsync(auctionId, user.Id, idempotencyKey: null, now, ct, fee);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new DomainException(ErrorCodes.AlreadyJoined, "This user has already joined this auction.");
        }

        await _audit.LogAsync(AuditActionType.ParticipantAddedByAdmin, nameof(AuctionParticipant),
            outcome.Participation.ParticipantId, null,
            new { AuctionId = auctionId, UserId = user.Id, EntryFee = fee }, ct: ct);

        await PublishJoinAsync(outcome, ct);

        return outcome.Participation;
    }

    private async Task<JoinOutcome> ExecuteJoinAsync(
        Guid auctionId, Guid userId, string? idempotencyKey, DateTime now, CancellationToken ct,
        decimal? entryFeeOverride = null)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);

        // Locking the auction row serialises the participant counter, so participant #400 is
        // unambiguous even when hundreds of people join at once.
        var auction = await _db.Auctions
            .FromSqlRaw("""SELECT *, xmin FROM "Auctions" WHERE "Id" = {0} FOR UPDATE""", auctionId)
            .FirstOrDefaultAsync(ct)
            ?? throw new DomainException(ErrorCodes.AuctionNotFound, "Auction not found.");

        if (!AuctionStateMachine.AcceptsRegistration(auction.Status))
        {
            throw new DomainException(
                ErrorCodes.RegistrationClosed,
                $"Registration is closed for this auction (status {auction.Status}).");
        }

        if (auction.ParticipantCount >= auction.RequiredParticipants)
        {
            throw new DomainException(ErrorCodes.AuctionFull, "This auction already has all its participants.");
        }

        // An administrator may set the fee for one participant; everyone else pays the auction's.
        // Refunds read EntryFeePaid, so a participant always gets back exactly what they paid.
        var entryFee = entryFeeOverride ?? auction.EntryFee;

        var participant = new AuctionParticipant
        {
            AuctionId = auctionId,
            UserId = userId,
            JoinedAt = now,
            EntryFeePaid = entryFee,
            Currency = auction.Currency,
            CurrentPrice = 0m,
            HighestPrice = 0m,
            Status = ParticipantStatus.Active
        };

        // Charge the entry fee from the Main wallet. The Shopping wallet can never pay it.
        if (entryFee > 0)
        {
            var payment = new Payment
            {
                UserId = userId,
                AuctionId = auctionId,
                PaymentType = PaymentType.EntryFee,
                Amount = entryFee,
                Currency = auction.Currency,
                Provider = "WALLET",
                Status = PaymentStatus.Pending,
                IdempotencyKey = idempotencyKey,
                CreatedAt = now
            };
            _db.Payments.Add(payment);

            await _wallets.DebitAsync(new WalletMovement
            {
                UserId = userId,
                WalletType = WalletType.Main,
                Amount = entryFee,
                Currency = auction.Currency,
                TransactionType = WalletTransactionType.AuctionEntryFee,
                ReferenceType = "Auction",
                ReferenceId = auctionId,
                Description = $"Entry fee for auction {auction.AuctionNumber}",
                IdempotencyKey = idempotencyKey is null ? null : $"join:{auctionId}:{userId}:{idempotencyKey}"
            }, ct);

            payment.Status = PaymentStatus.Completed;
            payment.CompletedAt = now;
            participant.EntryFeePaymentId = payment.Id;
        }

        _db.AuctionParticipants.Add(participant);

        auction.ParticipantCount += 1;
        auction.UpdatedAt = now;

        if (auction.Status == AuctionStatus.RegistrationOpen)
        {
            auction.Status = AuctionStatus.WaitingForParticipants;
        }

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auctionId,
            UserId = userId,
            EventType = AuctionEventType.ParticipantJoined,
            Timestamp = now,
            NewValue = auction.ParticipantCount.ToString()
        });

        var thresholdReached = false;
        DateTime? scheduledStartAt = null;

        // The auction becomes ready only when the required participant count is met, never merely
        // because it was created.
        if (auction.ParticipantCount >= auction.RequiredParticipants &&
            auction.ParticipantsCompletedAt is null)
        {
            thresholdReached = true;
            auction.ParticipantsCompletedAt = now;
            auction.ScheduledStartAt = now.AddSeconds(auction.StartDelayAfterParticipantsSeconds);
            scheduledStartAt = auction.ScheduledStartAt;

            AuctionStateMachine.EnsureCanTransition(auction.Status, AuctionStatus.Ready);
            auction.Status = auction.StartDelayAfterParticipantsSeconds > 0
                ? AuctionStatus.WaitingForStart
                : AuctionStatus.Ready;

            _db.AuctionEvents.Add(new AuctionEvent
            {
                AuctionId = auctionId,
                EventType = AuctionEventType.RequiredParticipantsReached,
                Timestamp = now,
                NewValue = auction.ParticipantCount.ToString()
            });

            _db.AuctionEvents.Add(new AuctionEvent
            {
                AuctionId = auctionId,
                EventType = AuctionEventType.AuctionStartScheduled,
                Timestamp = now,
                NewValue = auction.ScheduledStartAt?.ToString("O")
            });
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "User {UserId} joined auction {AuctionId} ({Count}/{Required}).",
            userId, auctionId, auction.ParticipantCount, auction.RequiredParticipants);

        return new JoinOutcome(
            auctionId,
            Map(participant),
            auction.ParticipantCount,
            auction.RequiredParticipants,
            thresholdReached,
            now,
            scheduledStartAt);
    }

    private async Task PublishJoinAsync(JoinOutcome outcome, CancellationToken ct)
    {
        try
        {
            await _realtime.ParticipantCountChangedAsync(
                outcome.AuctionId, outcome.ParticipantCount, outcome.RequiredParticipants, ct);

            if (!outcome.ThresholdReached) return;

            await _realtime.AuctionReadyAsync(
                outcome.AuctionId, outcome.ParticipantCount, outcome.Now, ct);

            if (outcome.ScheduledStartAt is { } startAt)
            {
                await _realtime.AuctionStartScheduledAsync(outcome.AuctionId, startAt, ct);

                var userIds = await _db.AuctionParticipants
                    .AsNoTracking()
                    .Where(p => p.AuctionId == outcome.AuctionId)
                    .Select(p => p.UserId)
                    .ToListAsync(ct);

                await _notifications.NotifyManyAsync(userIds, new NotificationTemplate(
                    NotificationType.RequiredParticipantsReached,
                    NotificationKeys.RequiredParticipantsReachedTitle,
                    NotificationKeys.RequiredParticipantsReachedBody,
                    new object[] { outcome.RequiredParticipants },
                    new Dictionary<string, string>
                    {
                        ["type"] = "REQUIRED_PARTICIPANTS_REACHED",
                        ["auctionId"] = outcome.AuctionId.ToString(),
                        ["scheduledStartAt"] = startAt.ToString("O")
                    }), ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish join events for auction {AuctionId}.", outcome.AuctionId);
        }
    }

    public async Task<MyParticipationDto?> GetMyParticipationAsync(
        Guid auctionId, Guid userId, CancellationToken ct = default)
    {
        var participant = await _db.AuctionParticipants
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.AuctionId == auctionId && p.UserId == userId, ct);

        return participant is null ? null : Map(participant);
    }

    private static MyParticipationDto Map(AuctionParticipant p) => new(
        p.Id, p.AuctionId, p.JoinedAt, p.EntryFeePaid, p.Currency,
        p.HighestPrice, p.HighestPriceAt, p.CurrentRank, p.BidCount, p.LastBidAt, p.Status);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.GetType().Name == "PostgresException" &&
        (ex.InnerException.Data["SqlState"] as string) == "23505";

    private sealed record JoinOutcome(
        Guid AuctionId,
        MyParticipationDto Participation,
        int ParticipantCount,
        int RequiredParticipants,
        bool ThresholdReached,
        DateTime Now,
        DateTime? ScheduledStartAt);
}
