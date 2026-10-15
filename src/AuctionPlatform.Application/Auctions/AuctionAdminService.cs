using AuctionPlatform.Application.Bidding;
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
    private readonly IAuctionCompletionService _completion;
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
        IAuctionCompletionService completion,
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
        _completion = completion;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AuctionDetailDto> CreateAsync(CreateAuctionRequest request, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var currency = request.Currency ?? _options.DefaultCurrency;

        var main = RequireMainPrize(request.Prizes);

        var targetPercentage = request.TargetPercentage ?? _options.DefaultTargetPercentage;
        var finalPhasePercentage = request.FinalPhasePercentage ?? _options.DefaultFinalPhasePercentage;

        var auction = new Auction
        {
            AuctionNumber = await _numbers.NextAuctionNumberAsync(ct),
            Title = request.Title,
            Description = request.Description,
            MainProductNameSnapshot = main.Name.Trim(),
            MainImageSnapshot = Blank(main.ImageUrl),
            CoverImageUrl = Blank(request.CoverImageUrl),
            // The auction is worth what its first prize is worth.
            OriginalPrice = Money.Round(main.Price),
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

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The rank 1 prize, which is the item being bid on and the source of the auction's value.
    /// </summary>
    private static AuctionPrizeRequest RequireMainPrize(IReadOnlyList<AuctionPrizeRequest>? prizes)
    {
        var main = prizes?.FirstOrDefault(p => p.Rank == 1)
            ?? throw new DomainException(
                ErrorCodes.InvalidAuctionConfiguration, "A rank 1 prize is required.");

        if (string.IsNullOrWhiteSpace(main.Name))
        {
            throw new DomainException(
                ErrorCodes.InvalidAuctionConfiguration, "The rank 1 prize must have a name.");
        }

        if (main.Price <= 0)
        {
            throw new DomainException(
                ErrorCodes.InvalidAuctionConfiguration,
                "The rank 1 prize price must be greater than zero; it sets the auction's value.");
        }

        return main;
    }

    /// <summary>
    /// Replaces the auction's prizes with the ones supplied.
    ///
    /// Each prize carries its own name, price and picture, so nothing is looked up from a shared
    /// catalog and no later edit elsewhere can change what this auction awards. Ranks must be
    /// unique, which the unique index on (AuctionId, Rank) also enforces.
    /// </summary>
    private async Task AttachPrizesAsync(
        Auction auction, IReadOnlyList<AuctionPrizeRequest>? prizes, CancellationToken ct)
    {
        if (prizes is null) return;

        var duplicateRank = prizes.GroupBy(p => p.Rank).FirstOrDefault(g => g.Count() > 1);
        if (duplicateRank is not null)
        {
            throw new DomainException(
                ErrorCodes.InvalidAuctionConfiguration,
                $"Rank {duplicateRank.Key} was given to more than one prize.");
        }

        var existing = await _db.AuctionPrizes.Where(p => p.AuctionId == auction.Id).ToListAsync(ct);
        _db.AuctionPrizes.RemoveRange(existing);

        foreach (var prize in prizes)
        {
            if (string.IsNullOrWhiteSpace(prize.Name))
            {
                throw new DomainException(
                    ErrorCodes.InvalidAuctionConfiguration,
                    $"The prize at rank {prize.Rank} must have a name.");
            }

            if (prize.Price < 0)
            {
                throw new DomainException(
                    ErrorCodes.InvalidAuctionConfiguration,
                    $"The prize at rank {prize.Rank} cannot have a negative price.");
            }

            _db.AuctionPrizes.Add(new AuctionPrize
            {
                AuctionId = auction.Id,
                Rank = prize.Rank,
                OriginalPrice = Money.Round(prize.Price),
                ProductNameSnapshot = prize.Name.Trim(),
                ProductImageSnapshot = Blank(prize.ImageUrl),
                Description = Blank(prize.Description),
                CreatedAt = _clock.UtcNow
            });
        }

        // The auction's own headline follows its first prize, so the two can never disagree.
        var main = prizes.FirstOrDefault(p => p.Rank == 1);
        if (main is not null)
        {
            auction.MainProductNameSnapshot = main.Name.Trim();
            auction.MainImageSnapshot = Blank(main.ImageUrl);
            auction.OriginalPrice = Money.Round(main.Price);
        }
    }

    public async Task<AuctionDetailDto> SetFeaturedAsync(
        Guid auctionId, bool featured, CancellationToken ct = default)
    {
        var auction = await GetTrackedAsync(auctionId, ct);

        // Taking one *out* is always allowed — that is how a mistake is undone, whatever state the
        // auction has reached since.
        if (featured && !AuctionStateMachine.CanBeFeatured(auction.Status))
        {
            throw new DomainException(
                ErrorCodes.InvalidAuctionState,
                $"Only an auction open to join or running can be featured (this one is {auction.Status}).");
        }

        if (auction.IsFeatured != featured)
        {
            auction.IsFeatured = featured;
            auction.UpdatedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(AuditActionType.AuctionUpdated, nameof(Auction), auction.Id,
                new { IsFeatured = !featured }, new { IsFeatured = featured }, ct: ct);
        }

        return await _reader.GetAsync(auctionId, null, ct);
    }

    public async Task ReorderFeaturedAsync(IReadOnlyList<Guid> auctionIds, CancellationToken ct = default)
    {
        if (auctionIds is null || auctionIds.Count == 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "No auctions were given to arrange.");
        }

        var ordered = auctionIds.Distinct().ToList();

        if (ordered.Count != auctionIds.Count)
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed, "The same auction was listed more than once.");
        }

        await using var tx = await _db.BeginTransactionAsync(ct);

        var auctions = await _db.Auctions.Where(a => ordered.Contains(a.Id)).ToListAsync(ct);

        if (auctions.Count != ordered.Count)
        {
            throw new DomainException(
                ErrorCodes.AuctionNotFound, "One of the auctions no longer exists.");
        }

        for (var position = 0; position < ordered.Count; position++)
        {
            auctions.First(a => a.Id == ordered[position]).FeaturedOrder = position;
        }

        await _db.SaveChangesAsync(ct);

        /*
         * Anything featured but not named goes behind the arranged ones.
         *
         * Without this, an auction featured from another browser tab while this arrangement was
         * being dragged would keep its position of zero and jump to the front of a row somebody
         * had just put in order. They all share one position here, and the query's newest-first
         * tie-break settles the rest.
         */
        await _db.Auctions
            .Where(a => a.IsFeatured && !ordered.Contains(a.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.FeaturedOrder, ordered.Count), ct);

        await tx.CommitAsync(ct);

        await _audit.LogAsync(
            AuditActionType.AuctionUpdated, nameof(Auction), ordered[0],
            null, new { FeaturedOrder = ordered }, ct: ct);

        _logger.LogInformation("The featured row was rearranged: {Count} auctions.", ordered.Count);
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
        if (request.CoverImageUrl is not null) auction.CoverImageUrl = Blank(request.CoverImageUrl);
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

        if (request.Prizes is not null)
        {
            await AttachPrizesAsync(auction, request.Prizes, ct);
        }

        // After the prizes, never before: replacing them can move OriginalPrice, and the price
        // ladder has to be derived from the value that ends up stored.
        auction.TargetPrice = AuctionRules.CalculateTargetPrice(auction.OriginalPrice, auction.TargetPercentage);
        auction.FinalPhasePrice = AuctionRules.CalculateFinalPhasePrice(auction.OriginalPrice, auction.FinalPhasePercentage);
        auction.UpdatedAt = _clock.UtcNow;

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

        // Called off for good, so it leaves the featured row with everything else that has ended.
        auction.IsFeatured = false;
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

        // Cancelling is the platform pulling the auction, so nobody should be out of pocket for it.
        // Entry fees go back as cash. Failures are logged rather than thrown: the cancellation is
        // already committed and must not be reported as failed, and the refund pass is idempotent
        // so it can safely be run again.
        try
        {
            await _completion.RefundCancelledAuctionAsync(auctionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Auction {AuctionId} was cancelled but entry fee refunds did not complete.", auctionId);
        }

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
