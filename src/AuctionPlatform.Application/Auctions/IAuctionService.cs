using AuctionPlatform.Application.Common;

namespace AuctionPlatform.Application.Auctions;

public interface IAuctionService
{
    Task<PagedResult<AuctionListItemDto>> ListAsync(AuctionListQuery query, Guid? userId, CancellationToken ct = default);
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
