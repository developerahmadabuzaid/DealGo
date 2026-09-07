using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Auctions;

/// <summary>Read-side auction queries. Everything returned here is derived from the database.</summary>
public sealed class AuctionService : IAuctionService
{
    private readonly IApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;

    public AuctionService(IApplicationDbContext db, IDateTimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<PagedResult<AuctionListItemDto>> ListAsync(
        AuctionListQuery query, Guid? userId, CancellationToken ct = default)
    {
        var q = _db.Auctions.AsNoTracking().Where(a => a.Status != AuctionStatus.Draft);

        if (query.Status.HasValue)
        {
            q = q.Where(a => a.Status == query.Status.Value);
        }

        q = query.Group?.ToLowerInvariant() switch
        {
            "live" => q.Where(a => a.Status == AuctionStatus.Live || a.Status == AuctionStatus.FinalPhase),
            "upcoming" => q.Where(a =>
                a.Status == AuctionStatus.RegistrationOpen ||
                a.Status == AuctionStatus.WaitingForParticipants ||
                a.Status == AuctionStatus.Ready ||
                a.Status == AuctionStatus.WaitingForStart ||
                a.Status == AuctionStatus.Scheduled),
            "completed" => q.Where(a =>
                a.Status == AuctionStatus.Finished ||
                a.Status == AuctionStatus.WinnersDeclared ||
                a.Status == AuctionStatus.PrizeClaim ||
                a.Status == AuctionStatus.Completed),
            _ => q
        };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(a => a.Title.ToLower().Contains(term) ||
                             a.AuctionNumber.ToLower().Contains(term));
        }

        var total = await q.LongCountAsync(ct);

        var rows = await q
            .OrderByDescending(a => a.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(a => new
            {
                a.Id, a.AuctionNumber, a.Title, a.MainProductNameSnapshot,
                MainImage = a.MainProduct != null ? a.MainProduct.PrimaryImageUrl : null,
                a.OriginalPrice, a.StartingPrice, a.CurrentPrice, a.TargetPrice,
                a.EntryFee, a.Currency, a.ParticipantCount, a.RequiredParticipants,
                a.Status, a.ScheduledStartAt, a.ActualStartedAt, a.DurationSeconds
            })
            .ToListAsync(ct);

        var now = _clock.UtcNow;

        var items = rows.Select(a => new AuctionListItemDto(
            a.Id, a.AuctionNumber, a.Title, a.MainImage, a.MainProductNameSnapshot,
            a.OriginalPrice, a.StartingPrice, a.CurrentPrice, a.TargetPrice,
            a.EntryFee, a.Currency, a.ParticipantCount, a.RequiredParticipants,
            a.Status, a.ScheduledStartAt, a.ActualStartedAt,
            RemainingSeconds(a.ActualStartedAt, a.DurationSeconds, a.Status, now))).ToList();

        return PagedResult<AuctionListItemDto>.Create(items, query.Page, query.PageSize, total);
    }

    private static int? RemainingSeconds(DateTime? startedAt, int durationSeconds, AuctionStatus status, DateTime now)
    {
        if (startedAt is null || !AuctionStateMachine.AcceptsBids(status)) return null;
        var remaining = (int)Math.Ceiling((startedAt.Value.AddSeconds(durationSeconds) - now).TotalSeconds);
        return remaining < 0 ? 0 : remaining;
    }

    public async Task<AuctionDetailDto> GetAsync(Guid auctionId, Guid? userId, CancellationToken ct = default)
    {
        var auction = await _db.Auctions
            .AsNoTracking()
            .Include(a => a.MainProduct)
            .FirstOrDefaultAsync(a => a.Id == auctionId, ct)
            ?? throw new DomainException(ErrorCodes.AuctionNotFound, "Auction not found.");

        var prizes = await _db.AuctionPrizes
            .AsNoTracking()
            .Where(p => p.AuctionId == auctionId)
            .OrderBy(p => p.Rank)
            .Select(p => new AuctionPrizeDto(
                p.Id, p.ProductId, p.Rank, p.ProductNameSnapshot,
                p.ProductImageSnapshot, p.OriginalPrice, auction.Currency))
            .ToListAsync(ct);

        AuctionParticipant? me = null;
        if (userId.HasValue)
        {
            me = await _db.AuctionParticipants
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.AuctionId == auctionId && p.UserId == userId.Value, ct);
        }

        int? myRank = null;
        if (me is not null && me.BidCount > 0)
        {
            myRank = await ComputeRankAsync(auctionId, me, ct);
        }

        return new AuctionDetailDto(
            auction.Id, auction.AuctionNumber, auction.Title, auction.Description,
            auction.MainProductNameSnapshot, auction.MainProduct?.PrimaryImageUrl,
            auction.OriginalPrice, auction.StartingPrice, auction.CurrentPrice,
            auction.TargetPercentage, auction.TargetPrice,
            auction.FinalPhasePercentage, auction.FinalPhasePrice,
            auction.BidIncrement, auction.EntryFee, auction.Currency,
            auction.ParticipantCount, auction.RequiredParticipants,
            auction.StartDelayAfterParticipantsSeconds, auction.DurationSeconds,
            auction.ParticipantsCompletedAt, auction.ScheduledStartAt, auction.ActualStartedAt,
            auction.FinalPhaseStartedAt, auction.FinishedAt, auction.EndsAt,
            auction.RemainingSeconds, auction.Status, auction.ExpirationPolicy,
            auction.TargetReached,
            AuctionRules.Progress(auction.StartingPrice, auction.CurrentPrice, auction.TargetPrice),
            prizes,
            me is not null, myRank, me?.HighestPrice, me?.BidCount,
            _clock.UtcNow);
    }

    /// <summary>
    /// The resync payload. A client that lost its connection refetches this rather than trying to
    /// replay missed events.
    /// </summary>
    public async Task<AuctionSnapshotDto> GetSnapshotAsync(Guid auctionId, Guid? userId, CancellationToken ct = default)
    {
        var auction = await _db.Auctions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == auctionId, ct)
            ?? throw new DomainException(ErrorCodes.AuctionNotFound, "Auction not found.");

        AuctionParticipant? me = null;
        if (userId.HasValue)
        {
            me = await _db.AuctionParticipants
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.AuctionId == auctionId && p.UserId == userId.Value, ct);
        }

        int? myRank = me is { BidCount: > 0 } ? await ComputeRankAsync(auctionId, me, ct) : null;
        var leaderboard = await GetLeaderboardAsync(auctionId, userId, 10, ct);

        return new AuctionSnapshotDto(
            auction.Id, auction.Status, auction.CurrentPrice, auction.StartingPrice,
            auction.TargetPrice, auction.FinalPhasePrice, auction.BidIncrement, auction.Currency,
            auction.ParticipantCount, auction.RequiredParticipants,
            auction.RemainingSeconds, auction.EndsAt, auction.TargetReached,
            AuctionRules.Progress(auction.StartingPrice, auction.CurrentPrice, auction.TargetPrice),
            myRank, me?.HighestPrice, leaderboard, auction.LastSequenceNumber,
            _clock.UtcNow);
    }

    public async Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(
        Guid auctionId, Guid? userId, int take, CancellationToken ct = default)
    {
        take = take is < 1 or > 100 ? 10 : take;

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
            i + 1, r.Id, r.UserId, BidService.MaskName(r.Name), r.HighestPrice,
            r.HighestPriceAt, r.BidCount, userId.HasValue && r.UserId == userId.Value)).ToList();
    }

    public async Task<IReadOnlyList<WinnerDto>> GetWinnersAsync(Guid auctionId, CancellationToken ct = default)
    {
        var rows = await _db.Winners
            .AsNoTracking()
            .Where(w => w.AuctionId == auctionId)
            .OrderBy(w => w.Rank)
            .Select(w => new
            {
                w.Id, w.Rank, w.UserId, w.HighestPrice, w.HighestPriceAt, w.PrizeId,
                PrizeName = w.Prize != null ? w.Prize.ProductNameSnapshot : string.Empty,
                PrizeImage = w.Prize != null ? w.Prize.ProductImageSnapshot : null,
                PrizeValue = w.Prize != null ? w.Prize.OriginalPrice : 0m,
                Name = w.Participant != null && w.Participant.User != null ? w.Participant.User.FullName : null,
                w.PrizeClaimId,
                // PrizeClaim.WinnerId owns the relationship, so the status is read from there.
                ClaimStatus = _db.PrizeClaims
                    .Where(c => c.WinnerId == w.Id)
                    .Select(c => (PrizeClaimStatus?)c.Status)
                    .FirstOrDefault(),
                Currency = w.Auction != null ? w.Auction.Currency : Money.DefaultCurrency
            })
            .ToListAsync(ct);

        return rows.Select(r => new WinnerDto(
            r.Id, r.Rank, r.UserId, BidService.MaskName(r.Name), r.HighestPrice, r.HighestPriceAt,
            r.PrizeId, r.PrizeName, r.PrizeImage, r.PrizeValue, r.Currency,
            r.PrizeClaimId, r.ClaimStatus)).ToList();
    }

    public async Task<PagedResult<ParticipantAdminDto>> GetParticipantsAsync(
        Guid auctionId, PagedQuery query, CancellationToken ct = default)
    {
        var q = _db.AuctionParticipants.AsNoTracking().Where(p => p.AuctionId == auctionId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(p => p.User != null &&
                             (p.User.FullName.ToLower().Contains(term) ||
                              (p.User.Email != null && p.User.Email.ToLower().Contains(term))));
        }

        var total = await q.LongCountAsync(ct);

        var items = await q
            .OrderByDescending(p => p.HighestPrice)
            .ThenBy(p => p.HighestPriceAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(p => new ParticipantAdminDto(
                p.Id, p.UserId,
                p.User != null ? p.User.FullName : string.Empty,
                p.User != null ? p.User.Email : null,
                p.JoinedAt, p.EntryFeePaid, p.Currency,
                p.HighestPrice, p.HighestPriceAt, p.BidCount, p.CurrentRank, p.Status))
            .ToListAsync(ct);

        return PagedResult<ParticipantAdminDto>.Create(items, query.Page, query.PageSize, total);
    }

    private async Task<int> ComputeRankAsync(Guid auctionId, AuctionParticipant me, CancellationToken ct)
    {
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
}
