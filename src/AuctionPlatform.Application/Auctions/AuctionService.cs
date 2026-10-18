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
        AuctionListQuery query, Guid? userId, bool includeDrafts = false, CancellationToken ct = default)
    {
        var q = _db.Auctions.AsNoTracking();

        // A draft is not public. It is, however, the whole point of the admin list: an auction is
        // created as a draft and has to be found again before it can be published.
        if (!includeDrafts)
        {
            q = q.Where(a => a.Status != AuctionStatus.Draft);
        }

        if (query.Status.HasValue)
        {
            q = q.Where(a => a.Status == query.Status.Value);
        }

        q = query.Group?.ToLowerInvariant() switch
        {
            // Only ever reachable from the admin console, which is the only caller that asks for
            // drafts at all; a public request filtered to "draft" still matches nothing.
            "draft" => q.Where(a => a.Status == AuctionStatus.Draft),
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

        if (query.Featured == true)
        {
            /*
             * Featured *and* still open.
             *
             * The flag alone is not enough. An auction finishes on a background worker's clock, not
             * on an administrator's, so between the last bid and anybody noticing there is always a
             * window where a finished auction still carries the mark. Filtering here means the row
             * empties itself the second the auction ends, and no stale flag can ever put a closed
             * door on the front page.
             *
             * Written out rather than calling AuctionStateMachine.CanBeFeatured because this has to
             * reach the database as SQL, and EF cannot translate a method call.
             */
            q = q.Where(a => a.IsFeatured && (
                a.Status == AuctionStatus.RegistrationOpen ||
                a.Status == AuctionStatus.WaitingForParticipants ||
                a.Status == AuctionStatus.Ready ||
                a.Status == AuctionStatus.WaitingForStart ||
                a.Status == AuctionStatus.Live ||
                a.Status == AuctionStatus.FinalPhase));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(a => a.Title.ToLower().Contains(term) ||
                             a.AuctionNumber.ToLower().Contains(term));
        }

        var total = await q.LongCountAsync(ct);

        /*
         * The featured row is the one list an administrator arranges by hand, so it is the one
         * list that is not simply newest first. Everything never dragged sits at zero and falls
         * back to newest first, which is what an auction featured a moment ago expects.
         */
        var ordered = query.Featured == true
            ? q.OrderBy(a => a.FeaturedOrder).ThenByDescending(a => a.CreatedAt)
            : q.OrderByDescending(a => a.CreatedAt);

        var rows = await ordered
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(a => new
            {
                a.Id, a.AuctionNumber, a.Title, a.MainProductNameSnapshot,
                MainImage = a.MainImageSnapshot,
                a.CoverImageUrl,
                a.IsFeatured,
                a.OriginalPrice, a.StartingPrice, a.CurrentPrice, a.TargetPrice,
                a.EntryFee, a.Currency, a.ParticipantCount, a.RequiredParticipants,
                a.Status, a.ScheduledStartAt, a.ActualStartedAt, a.DurationSeconds,
                a.FinishedAt,
                // Whoever stood highest when it closed. Only asked of a finished auction: on a running
                // one the name would be a guess about an outcome nobody knows yet.
                WinnerName = (a.Status == AuctionStatus.Finished ||
                              a.Status == AuctionStatus.WinnersDeclared ||
                              a.Status == AuctionStatus.PrizeClaim ||
                              a.Status == AuctionStatus.Completed)
                    ? _db.AuctionParticipants
                        .Where(p => p.AuctionId == a.Id && p.BidCount > 0)
                        .OrderByDescending(p => p.HighestPrice)
                        .ThenBy(p => p.HighestPriceAt)
                        .Select(p => p.User != null ? p.User.FullName : string.Empty)
                        .FirstOrDefault()
                    : null
            })
            .ToListAsync(ct);

        var now = _clock.UtcNow;

        var items = rows.Select(a => new AuctionListItemDto(
            a.Id, a.AuctionNumber, a.Title, a.MainImage, a.MainProductNameSnapshot,
            a.OriginalPrice, a.StartingPrice, a.CurrentPrice, a.TargetPrice,
            a.EntryFee, a.Currency, a.ParticipantCount, a.RequiredParticipants,
            a.Status, a.ScheduledStartAt, a.ActualStartedAt,
            RemainingSeconds(a.ActualStartedAt, a.DurationSeconds, a.Status, now),
            a.CoverImageUrl, a.IsFeatured,
            a.WinnerName is null ? null : BidService.MaskName(a.WinnerName),
            a.FinishedAt)).ToList();

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
            .FirstOrDefaultAsync(a => a.Id == auctionId, ct)
            ?? throw new DomainException(ErrorCodes.AuctionNotFound, "Auction not found.");

        var prizes = await _db.AuctionPrizes
            .AsNoTracking()
            .Where(p => p.AuctionId == auctionId)
            .OrderBy(p => p.Rank)
            .Select(p => new AuctionPrizeDto(
                p.Id, p.ProductId, p.Rank, p.ProductNameSnapshot,
                p.ProductImageSnapshot, p.Description, p.OriginalPrice, auction.Currency))
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
            auction.MainProductNameSnapshot, auction.MainImageSnapshot,
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
            _clock.UtcNow,
            auction.CoverImageUrl,
            auction.IsFeatured,
            auction.StartingPercentage);
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

        // Who bid last, so a client can switch the bid button off for that participant until
        // someone else bids. The server enforces the rule either way.
        Guid? lastBidParticipantId = auction.LastSequenceNumber > 0
            ? await _db.Bids
                .AsNoTracking()
                .Where(b => b.AuctionId == auctionId && b.SequenceNumber == auction.LastSequenceNumber)
                .Select(b => (Guid?)b.ParticipantId)
                .FirstOrDefaultAsync(ct)
            : null;

        return new AuctionSnapshotDto(
            auction.Id, auction.Status, auction.CurrentPrice, auction.StartingPrice,
            auction.TargetPrice, auction.FinalPhasePrice, auction.BidIncrement, auction.Currency,
            auction.ParticipantCount, auction.RequiredParticipants,
            auction.RemainingSeconds, auction.EndsAt, auction.TargetReached,
            AuctionRules.Progress(auction.StartingPrice, auction.CurrentPrice, auction.TargetPrice),
            myRank, me?.HighestPrice, leaderboard, auction.LastSequenceNumber,
            _clock.UtcNow, me?.Id, lastBidParticipantId);
    }

    public async Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(
        Guid auctionId, Guid? userId, int take, CancellationToken ct = default)
    {
        take = take is < 1 or > 100 ? 10 : take;

        /*
         * Everyone who is in the auction, not only those who have already bid.
         *
         * Ranking by highest price alone hid the room until somebody moved: a participant watching
         * the first minute saw an empty board and no sign of the people they are bidding against.
         * Those who have bid come first, in price order; the rest follow in the order they joined,
         * with nothing claimed about a price they have not offered.
         */
        var rows = await _db.AuctionParticipants
            .AsNoTracking()
            .Where(p => p.AuctionId == auctionId)
            .OrderByDescending(p => p.BidCount > 0)
            .ThenByDescending(p => p.HighestPrice)
            .ThenBy(p => p.HighestPriceAt)
            .ThenBy(p => p.JoinedAt)
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

    public async Task<IReadOnlyList<BidHistoryEntryDto>> GetBidsAsync(
        Guid auctionId, Guid? userId, int take, CancellationToken ct = default)
    {
        take = take is < 1 or > 500 ? 100 : take;

        // Newest first: the closing bid at the top, then every one before it.
        var rows = await _db.Bids
            .AsNoTracking()
            .Where(b => b.AuctionId == auctionId)
            .OrderByDescending(b => b.SequenceNumber)
            .Take(take)
            .Select(b => new
            {
                b.SequenceNumber, b.NewPrice, b.ServerTimestamp, b.UserId,
                Name = b.Participant != null && b.Participant.User != null ? b.Participant.User.FullName : null
            })
            .ToListAsync(ct);

        return rows.Select(r => new BidHistoryEntryDto(
            r.SequenceNumber, r.NewPrice, BidService.MaskName(r.Name), r.ServerTimestamp,
            userId.HasValue && r.UserId == userId.Value)).ToList();
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
