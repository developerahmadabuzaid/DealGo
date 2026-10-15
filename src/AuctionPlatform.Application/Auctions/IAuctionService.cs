using AuctionPlatform.Application.Common;

namespace AuctionPlatform.Application.Auctions;

public interface IAuctionService
{
    /// <summary>
    /// Lists auctions.
    ///
    /// Drafts are hidden unless <paramref name="includeDrafts"/> says otherwise. It is a
    /// parameter rather than a field on the query so it cannot be set from a query string: the
    /// caller decides, and only the administrative controller passes true.
    /// </summary>
    Task<PagedResult<AuctionListItemDto>> ListAsync(
        AuctionListQuery query, Guid? userId, bool includeDrafts = false, CancellationToken ct = default);
    Task<AuctionDetailDto> GetAsync(Guid auctionId, Guid? userId, CancellationToken ct = default);
    Task<AuctionSnapshotDto> GetSnapshotAsync(Guid auctionId, Guid? userId, CancellationToken ct = default);
    Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(Guid auctionId, Guid? userId, int take, CancellationToken ct = default);
    Task<IReadOnlyList<WinnerDto>> GetWinnersAsync(Guid auctionId, CancellationToken ct = default);
    Task<PagedResult<ParticipantAdminDto>> GetParticipantsAsync(Guid auctionId, PagedQuery query, CancellationToken ct = default);
}

/// <summary>Administrative auction operations. Every one of these writes an audit record.</summary>
public interface IAuctionAdminService
{
    Task<AuctionDetailDto> CreateAsync(CreateAuctionRequest request, CancellationToken ct = default);
    /// <summary>Marks the auction as one of the front page's featured auctions, or unmarks it.</summary>
    Task<AuctionDetailDto> SetFeaturedAsync(Guid auctionId, bool featured, CancellationToken ct = default);

    /// <summary>
    /// Arranges the featured row: the auctions named take that order, front to back. Any featured
    /// auction left out keeps its place behind them.
    /// </summary>
    Task ReorderFeaturedAsync(IReadOnlyList<Guid> auctionIds, CancellationToken ct = default);

    Task<AuctionDetailDto> UpdateAsync(Guid auctionId, UpdateAuctionRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid auctionId, CancellationToken ct = default);

    Task<AuctionDetailDto> PublishAsync(Guid auctionId, CancellationToken ct = default);

    /// <summary>Starts the auction immediately, bypassing the scheduled start.</summary>
    Task<AuctionDetailDto> StartNowAsync(Guid auctionId, CancellationToken ct = default);

    Task<AuctionDetailDto> UpdateScheduleAsync(Guid auctionId, UpdateScheduleRequest request, CancellationToken ct = default);
    Task<AuctionDetailDto> PauseAsync(Guid auctionId, CancellationToken ct = default);
    Task<AuctionDetailDto> ResumeAsync(Guid auctionId, CancellationToken ct = default);
    Task<AuctionDetailDto> CancelAsync(Guid auctionId, string? reason, CancellationToken ct = default);
}

/// <summary>Joining an auction: entry fee payment plus the participant threshold bookkeeping.</summary>
public interface IParticipationService
{
    Task<MyParticipationDto> JoinAsync(Guid auctionId, Guid userId, string? idempotencyKey, CancellationToken ct = default);

    /// <summary>
    /// An administrator adds a user to the auction, at an entry fee the administrator chooses. The
    /// fee is taken from the user's Main wallet exactly as if they had joined themselves, so a user
    /// who cannot cover it is refused rather than let in on credit.
    /// </summary>
    Task<MyParticipationDto> AddByAdminAsync(
        Guid auctionId, AddParticipantRequest request, CancellationToken ct = default);
    Task<MyParticipationDto?> GetMyParticipationAsync(Guid auctionId, Guid userId, CancellationToken ct = default);
}

public sealed record ParticipantAdminDto(
    Guid Id,
    Guid UserId,
    string FullName,
    string? Email,
    DateTime JoinedAt,
    decimal EntryFeePaid,
    string Currency,
    decimal HighestPrice,
    DateTime? HighestPriceAt,
    int BidCount,
    int CurrentRank,
    Domain.Enums.ParticipantStatus Status);
