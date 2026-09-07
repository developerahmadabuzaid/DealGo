using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Application.Realtime;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Application.Auctions;

/// <summary>
/// Administrative auction lifecycle. Every mutation validates the state machine, writes an
/// auction event and an audit record, and only then broadcasts.
/// </summary>
public sealed class AuctionAdminService : IAuctionAdminService
{
    private readonly IApplicationDbContext _db;
    private readonly IAuctionService _reader;
    private readonly IDateTimeProvider _clock;
    private readonly INumberGenerator _numbers;
    private readonly IAuctionRealtimeNotifier _realtime;
    private readonly INotificationService _notifications;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _currentUser;
    private readonly AuctionOptions _options;
    private readonly ILogger<AuctionAdminService> _logger;

    public AuctionAdminService(
        IApplicationDbContext db,
        IAuctionService reader,
        IDateTimeProvider clock,
        INumberGenerator numbers,
        IAuctionRealtimeNotifier realtime,
        INotificationService notifications,
        IAuditService audit,
        ICurrentUserService currentUser,
        IOptions<AuctionOptions> options,
        ILogger<AuctionAdminService> logger)
    {
        _db = db;
        _reader = reader;
        _clock = clock;
        _numbers = numbers;
        _realtime = realtime;
        _notifications = notifications;
        _audit = audit;
        _currentUser = currentUser;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AuctionDetailDto> CreateAsync(CreateAuctionRequest request, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var currency = request.Currency ?? _options.DefaultCurrency;

        var mainProduct = await _db.Products.FirstOrDefaultAsync(p => p.Id == request.MainProductId, ct)
            ?? throw new DomainException(ErrorCodes.ProductNotFound, "The main product was not found.");

        var targetPercentage = request.TargetPercentage ?? _options.DefaultTargetPercentage;
        var finalPhasePercentage = request.FinalPhasePercentage ?? _options.DefaultFinalPhasePercentage;

        var auction = new Auction
        {
            AuctionNumber = await _numbers.NextAuctionNumberAsync(ct),
            Title = request.Title,
            Description = request.Description,
            MainProductId = request.MainProductId,
            MainProductNameSnapshot = mainProduct.Name,
            OriginalPrice = Money.Round(request.OriginalPrice),
            StartingPrice = Money.Round(request.StartingPrice),
            CurrentPrice = Money.Round(request.StartingPrice),
            TargetPercentage = targetPercentage,
            FinalPhasePercentage = finalPhasePercentage,
            BidIncrement = request.BidIncrement ?? _options.DefaultBidIncrement,
            EntryFee = request.EntryFee ?? _options.DefaultEntryFee,
            Currency = currency,
            RequiredParticipants = request.RequiredParticipants ?? _options.DefaultRequiredParticipants,
            StartDelayAfterParticipantsSeconds =
                request.StartDelayAfterParticipantsSeconds ?? _options.DefaultStartDelaySeconds,
            DurationSeconds = request.DurationSeconds ?? _options.DefaultDurationSeconds,
            ExpirationPolicy = request.ExpirationPolicy ?? _options.DefaultExpirationPolicy,
            RefundLosingParticipantsEntryFee = _options.RefundLosingParticipantsEntryFee,
            RefundWinnerEntryFee = _options.RefundWinnerEntryFee,
            RefundDestinationWallet = _options.RefundDestinationWallet,
            Status = AuctionStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Target and final-phase prices are computed once and stored; they are never recomputed
        // from the live catalog price later.
        auction.TargetPrice = AuctionRules.CalculateTargetPrice(auction.OriginalPrice, targetPercentage);
        auction.FinalPhasePrice = AuctionRules.CalculateFinalPhasePrice(auction.OriginalPrice, finalPhasePercentage);

        _db.Auctions.Add(auction);
        await AttachPrizesAsync(auction, request.Prizes, ct);

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auction.Id,
            UserId = _currentUser.UserId,
            EventType = AuctionEventType.AuctionCreated,
            Timestamp = now
        });

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.AuctionCreated, nameof(Auction), auction.Id,
            null, new { auction.AuctionNumber, auction.Title, auction.OriginalPrice }, ct: ct);

        return await _reader.GetAsync(auction.Id, null, ct);
    }

    private async Task AttachPrizesAsync(
        Auction auction, IReadOnlyList<AuctionPrizeRequest>? prizes, CancellationToken ct)
    {
        if (prizes is null) return;

        var existing = await _db.AuctionPrizes.Where(p => p.AuctionId == auction.Id).ToListAsync(ct);
        _db.AuctionPrizes.RemoveRange(existing);

        var productIds = prizes.Select(p => p.ProductId).Distinct().ToList();
        var products = await _db.Products
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var prize in prizes)
        {
            if (!products.TryGetValue(prize.ProductId, out var product))
            {
                throw new DomainException(
                    ErrorCodes.ProductNotFound, $"Prize product {prize.ProductId} was not found.");
            }

            _db.AuctionPrizes.Add(new AuctionPrize
            {
                AuctionId = auction.Id,
                ProductId = product.Id,
                Rank = prize.Rank,
                OriginalPrice = product.Price,
                ProductNameSnapshot = product.Name,
                ProductImageSnapshot = product.PrimaryImageUrl,
                CreatedAt = _clock.UtcNow
            });
        }
    }

    public async Task<AuctionDetailDto> UpdateAsync(
        Guid auctionId, UpdateAuctionRequest request, CancellationToken ct = default)
    {
        var auction = await GetTrackedAsync(auctionId, ct);

        if (auction.Status is not (AuctionStatus.Draft or AuctionStatus.Scheduled or AuctionStatus.RegistrationOpen))
        {
            throw new DomainException(
                ErrorCodes.InvalidAuctionState,
                "An auction can only be edited before participants start bidding.");
        }

        var before = new { auction.Title, auction.OriginalPrice, auction.StartingPrice, auction.EntryFee };

        if (request.Title is not null) auction.Title = request.Title;
        if (request.Description is not null) auction.Description = request.Description;
        if (request.OriginalPrice.HasValue) auction.OriginalPrice = Money.Round(request.OriginalPrice.Value);
        if (request.StartingPrice.HasValue)
        {
            auction.StartingPrice = Money.Round(request.StartingPrice.Value);
            if (auction.Status == AuctionStatus.Draft) auction.CurrentPrice = auction.StartingPrice;
        }
        if (request.TargetPercentage.HasValue) auction.TargetPercentage = request.TargetPercentage.Value;
        if (request.FinalPhasePercentage.HasValue) auction.FinalPhasePercentage = request.FinalPhasePercentage.Value;
        if (request.BidIncrement.HasValue) auction.BidIncrement = request.BidIncrement.Value;
        if (request.EntryFee.HasValue) auction.EntryFee = request.EntryFee.Value;
        if (request.RequiredParticipants.HasValue) auction.RequiredParticipants = request.RequiredParticipants.Value;
        if (request.StartDelayAfterParticipantsSeconds.HasValue)
            auction.StartDelayAfterParticipantsSeconds = request.StartDelayAfterParticipantsSeconds.Value;
        if (request.DurationSeconds.HasValue) auction.DurationSeconds = request.DurationSeconds.Value;
        if (request.ExpirationPolicy.HasValue) auction.ExpirationPolicy = request.ExpirationPolicy.Value;

        auction.TargetPrice = AuctionRules.CalculateTargetPrice(auction.OriginalPrice, auction.TargetPercentage);
        auction.FinalPhasePrice = AuctionRules.CalculateFinalPhasePrice(auction.OriginalPrice, auction.FinalPhasePercentage);
        auction.UpdatedAt = _clock.UtcNow;

        if (request.Prizes is not null)
        {
            await AttachPrizesAsync(auction, request.Prizes, ct);
        }

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auction.Id,
            UserId = _currentUser.UserId,
            EventType = AuctionEventType.AuctionUpdated,
            Timestamp = _clock.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.AuctionUpdated, nameof(Auction), auction.Id,
            before, new { auction.Title, auction.OriginalPrice, auction.StartingPrice, auction.EntryFee }, ct: ct);

        return await _reader.GetAsync(auctionId, null, ct);
    }

    public async Task DeleteAsync(Guid auctionId, CancellationToken ct = default)
    {
        var auction = await GetTrackedAsync(auctionId, ct);

        // Auctions with financial history are cancelled, never deleted.
        if (auction.Status != AuctionStatus.Draft)
        {
            await CancelAsync(auctionId, "Deleted by administrator", ct);
            return;
        }

        var prizes = await _db.AuctionPrizes.Where(p => p.AuctionId == auctionId).ToListAsync(ct);
        _db.AuctionPrizes.RemoveRange(prizes);
        _db.Auctions.Remove(auction);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.AuctionCancelled, nameof(Auction), auctionId,
            new { auction.AuctionNumber }, null, "Draft auction deleted", ct);
    }

    public async Task<AuctionDetailDto> PublishAsync(Guid auctionId, CancellationToken ct = default)
    {
        var auction = await GetTrackedAsync(auctionId, ct);

        var prizes = await _db.AuctionPrizes.Where(p => p.AuctionId == auctionId).ToListAsync(ct);

        // Publishing is the gate: exactly four correctly ranked prizes and a coherent price ladder.
        AuctionRules.EnsurePublishable(auction, prizes.Count, prizes.Select(p => p.Rank));
        AuctionStateMachine.EnsureCanTransition(auction.Status, AuctionStatus.RegistrationOpen);

        auction.Status = AuctionStatus.RegistrationOpen;
        auction.CurrentPrice = auction.StartingPrice;
        auction.UpdatedAt = _clock.UtcNow;

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auctionId,
            UserId = _currentUser.UserId,
            EventType = AuctionEventType.AuctionPublished,
            Timestamp = _clock.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.AuctionPublished, nameof(Auction), auctionId,
            null, new { auction.AuctionNumber }, ct: ct);

        return await _reader.GetAsync(auctionId, null, ct);
    }

    public async Task<AuctionDetailDto> StartNowAsync(Guid auctionId, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        await using (var tx = await _db.BeginTransactionAsync(ct))
        {
            var auction = await _db.Auctions
                .FromSqlRaw("""SELECT *, xmin FROM "Auctions" WHERE "Id" = {0} FOR UPDATE""", auctionId)
                .FirstOrDefaultAsync(ct)
                ?? throw new DomainException(ErrorCodes.AuctionNotFound, "Auction not found.");

            if (auction.Status is not (AuctionStatus.Ready or AuctionStatus.WaitingForStart))
            {
                throw new DomainException(
                    ErrorCodes.AuctionNotReady,
                    "The auction can only be started once the required participants have joined.");
            }

            AuctionStateMachine.EnsureCanTransition(auction.Status, AuctionStatus.Live);

            auction.Status = AuctionStatus.Live;
            auction.ActualStartedAt = now;
            auction.ScheduledStartAt = now;
            auction.UpdatedAt = now;

            _db.AuctionEvents.Add(new AuctionEvent
            {
                AuctionId = auctionId,
                UserId = _currentUser.UserId,
                EventType = AuctionEventType.AuctionStartedManually,
                Timestamp = now
            });
            _db.AuctionEvents.Add(new AuctionEvent
            {
                AuctionId = auctionId,
                EventType = AuctionEventType.AuctionStarted,
                Timestamp = now
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await SafeAsync(() => _realtime.AuctionStartedAsync(
                auctionId, now, now.AddSeconds(auction.DurationSeconds), auction.CurrentPrice, ct));
        }

        await NotifyParticipantsOfStartAsync(auctionId, ct);

        await _audit.LogAsync(AuditActionType.AuctionStarted, nameof(Auction), auctionId,
            null, new { StartedAt = now }, "Started manually", ct);

        return await _reader.GetAsync(auctionId, null, ct);
    }

    public async Task<AuctionDetailDto> UpdateScheduleAsync(
        Guid auctionId, UpdateScheduleRequest request, CancellationToken ct = default)
    {
        var auction = await GetTrackedAsync(auctionId, ct);

        if (auction.Status is not (AuctionStatus.Ready or AuctionStatus.WaitingForStart
            or AuctionStatus.RegistrationOpen or AuctionStatus.WaitingForParticipants
            or AuctionStatus.Scheduled or AuctionStatus.Draft))
        {
            throw new DomainException(
                ErrorCodes.InvalidAuctionState, "The start time can no longer be changed.");
        }

        var previous = auction.ScheduledStartAt;
        auction.ScheduledStartAt = request.ScheduledStartAt;
        auction.UpdatedAt = _clock.UtcNow;

        if (auction.Status == AuctionStatus.Ready)
        {
            auction.Status = AuctionStatus.WaitingForStart;
        }

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auctionId,
            UserId = _currentUser.UserId,
            EventType = AuctionEventType.AuctionStartTimeChanged,
            Timestamp = _clock.UtcNow,
            OldValue = previous?.ToString("O"),
            NewValue = request.ScheduledStartAt.ToString("O"),
            Metadata = request.Reason
        });

        await _db.SaveChangesAsync(ct);

        await SafeAsync(() => _realtime.AuctionStartTimeChangedAsync(
            auctionId, previous, request.ScheduledStartAt, ct));

        var userIds = await _db.AuctionParticipants
            .AsNoTracking().Where(p => p.AuctionId == auctionId).Select(p => p.UserId).ToListAsync(ct);

        await SafeAsync(() => _notifications.NotifyManyAsync(userIds, new NotificationTemplate(
            NotificationType.AuctionStartTimeChanged,
            NotificationKeys.AuctionStartTimeChangedTitle,
            NotificationKeys.AuctionStartTimeChangedBody,
            null,
            new Dictionary<string, string>
            {
                ["type"] = "AUCTION_START_TIME_CHANGED",
                ["auctionId"] = auctionId.ToString(),
                ["scheduledStartAt"] = request.ScheduledStartAt.ToString("O")
            }), ct));

        await _audit.LogAsync(AuditActionType.AuctionScheduleChanged, nameof(Auction), auctionId,
            new { Previous = previous }, new { New = request.ScheduledStartAt }, request.Reason, ct);

        return await _reader.GetAsync(auctionId, null, ct);
    }

    public async Task<AuctionDetailDto> PauseAsync(Guid auctionId, CancellationToken ct = default)
    {
        var auction = await GetTrackedAsync(auctionId, ct);
        AuctionStateMachine.EnsureCanTransition(auction.Status, AuctionStatus.Paused);

        auction.Status = AuctionStatus.Paused;
        auction.UpdatedAt = _clock.UtcNow;

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auctionId,
            UserId = _currentUser.UserId,
            EventType = AuctionEventType.AuctionPaused,
            Timestamp = _clock.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        await SafeAsync(() => _realtime.AuctionPausedAsync(auctionId, ct));
        await _audit.LogAsync(AuditActionType.AuctionPaused, nameof(Auction), auctionId, ct: ct);

        return await _reader.GetAsync(auctionId, null, ct);
    }

    public async Task<AuctionDetailDto> ResumeAsync(Guid auctionId, CancellationToken ct = default)
    {
        var auction = await GetTrackedAsync(auctionId, ct);

        if (auction.Status != AuctionStatus.Paused)
        {
            throw new DomainException(ErrorCodes.InvalidAuctionState, "The auction is not paused.");
        }

        auction.Status = auction.ActualStartedAt is null
            ? AuctionStatus.WaitingForStart
            : auction.CurrentPrice >= auction.FinalPhasePrice
                ? AuctionStatus.FinalPhase
                : AuctionStatus.Live;

        auction.UpdatedAt = _clock.UtcNow;

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auctionId,
            UserId = _currentUser.UserId,
            EventType = AuctionEventType.AuctionResumed,
            Timestamp = _clock.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        await SafeAsync(() => _realtime.AuctionResumedAsync(auctionId, auction.EndsAt, ct));
        await _audit.LogAsync(AuditActionType.AuctionResumed, nameof(Auction), auctionId, ct: ct);

        return await _reader.GetAsync(auctionId, null, ct);
    }

    public async Task<AuctionDetailDto> CancelAsync(Guid auctionId, string? reason, CancellationToken ct = default)
    {
        var auction = await GetTrackedAsync(auctionId, ct);
        AuctionStateMachine.EnsureCanTransition(auction.Status, AuctionStatus.Cancelled);

        auction.Status = AuctionStatus.Cancelled;
        auction.FinishedAt = _clock.UtcNow;
        auction.UpdatedAt = _clock.UtcNow;

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auctionId,
            UserId = _currentUser.UserId,
            EventType = AuctionEventType.AuctionCancelled,
            Timestamp = _clock.UtcNow,
            Metadata = reason
        });

        await _db.SaveChangesAsync(ct);

        await SafeAsync(() => _realtime.AuctionCancelledAsync(auctionId, reason, ct));
        await _audit.LogAsync(AuditActionType.AuctionCancelled, nameof(Auction), auctionId,
            null, null, reason, ct);

        _logger.LogInformation("Auction {AuctionId} cancelled: {Reason}", auctionId, reason);

        return await _reader.GetAsync(auctionId, null, ct);
    }

    private async Task NotifyParticipantsOfStartAsync(Guid auctionId, CancellationToken ct)
    {
        var userIds = await _db.AuctionParticipants
            .AsNoTracking().Where(p => p.AuctionId == auctionId).Select(p => p.UserId).ToListAsync(ct);

        await SafeAsync(() => _notifications.NotifyManyAsync(userIds, new NotificationTemplate(
            NotificationType.AuctionStarted,
            NotificationKeys.AuctionStartedTitle,
            NotificationKeys.AuctionStartedBody,
            null,
            new Dictionary<string, string>
            {
                ["type"] = "AUCTION_STARTED",
                ["auctionId"] = auctionId.ToString()
            }), ct));
    }

    private async Task<Auction> GetTrackedAsync(Guid auctionId, CancellationToken ct) =>
        await _db.Auctions.FirstOrDefaultAsync(a => a.Id == auctionId, ct)
        ?? throw new DomainException(ErrorCodes.AuctionNotFound, "Auction not found.");

    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Post-commit side effect failed."); }
    }
}
