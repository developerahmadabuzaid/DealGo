using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Application.Auctions;

public sealed record AuctionPrizeDto(
    Guid Id,
    Guid ProductId,
    int Rank,
    string ProductName,
    string? ImageUrl,
    decimal OriginalPrice,
    string Currency);

/// <summary>Public list projection. Deliberately omits anything per-user.</summary>
public sealed record AuctionListItemDto(
    Guid Id,
    string AuctionNumber,
    string Title,
    string? MainImageUrl,
    string MainProductName,
    decimal OriginalPrice,
    decimal StartingPrice,
    decimal CurrentPrice,
    decimal TargetPrice,
    decimal EntryFee,
    string Currency,
    int ParticipantCount,
    int RequiredParticipants,
    AuctionStatus Status,
    DateTime? ScheduledStartAt,
    DateTime? ActualStartedAt,
    int? RemainingSeconds);

/// <summary>
/// Full auction detail. Fields prefixed My* are resolved from the caller's participation and are
/// null for anonymous callers.
/// </summary>
public sealed record AuctionDetailDto(
    Guid Id,
    string AuctionNumber,
    string Title,
    string? Description,
    string MainProductName,
    string? MainImageUrl,
    decimal OriginalPrice,
    decimal StartingPrice,
    decimal CurrentPrice,
    decimal TargetPercentage,
    decimal TargetPrice,
    decimal FinalPhasePercentage,
    decimal FinalPhasePrice,
    decimal BidIncrement,
    decimal EntryFee,
    string Currency,
    int ParticipantCount,
    int RequiredParticipants,
    int StartDelayAfterParticipantsSeconds,
    int DurationSeconds,
    DateTime? ParticipantsCompletedAt,
    DateTime? ScheduledStartAt,
    DateTime? ActualStartedAt,
    DateTime? FinalPhaseStartedAt,
    DateTime? FinishedAt,
    DateTime? EndsAt,
    int? RemainingSeconds,
    AuctionStatus Status,
    ExpirationPolicy ExpirationPolicy,
    bool TargetReached,
    decimal Progress,
    IReadOnlyList<AuctionPrizeDto> Prizes,
    bool HasJoined,
    int? MyRank,
    decimal? MyHighestPrice,
    int? MyBidCount,
    DateTime ServerTime);

/// <summary>
/// The authoritative state a reconnecting client resynchronises against. Clients never
/// reconstruct missed events; they refetch this and continue from it.
/// </summary>
public sealed record AuctionSnapshotDto(
    Guid Id,
    AuctionStatus Status,
    decimal CurrentPrice,
    decimal StartingPrice,
    decimal TargetPrice,
    decimal FinalPhasePrice,
    decimal BidIncrement,
    string Currency,
    int ParticipantCount,
    int RequiredParticipants,
    int? RemainingSeconds,
    DateTime? EndsAt,
    bool TargetReached,
    decimal Progress,
    int? MyRank,
    decimal? MyHighestPrice,
    IReadOnlyList<LeaderboardEntryDto> Leaderboard,
    long LastSequenceNumber,
    DateTime ServerTime);

public sealed record LeaderboardEntryDto(
    int Rank,
    Guid ParticipantId,
    Guid UserId,
    string DisplayName,
    decimal HighestPrice,
    DateTime? HighestPriceAt,
    int BidCount,
    bool IsMe);

public sealed record MyParticipationDto(
    Guid ParticipantId,
    Guid AuctionId,
    DateTime JoinedAt,
    decimal EntryFeePaid,
    string Currency,
    decimal HighestPrice,
    DateTime? HighestPriceAt,
    int CurrentRank,
    int BidCount,
    DateTime? LastBidAt,
    ParticipantStatus Status);

public sealed record BidResultDto(
    Guid BidId,
    Guid AuctionId,
    long SequenceNumber,
    decimal PreviousPrice,
    decimal NewPrice,
    decimal TargetPrice,
    bool ReachedTarget,
    bool FinalPhase,
    AuctionStatus AuctionStatus,
    int MyRank,
    decimal MyHighestPrice,
    DateTime ServerTimestamp,
    bool WasDuplicate);

public sealed record WinnerDto(
    Guid Id,
    int Rank,
    Guid UserId,
    string DisplayName,
    decimal HighestPrice,
    DateTime? HighestPriceAt,
    Guid PrizeId,
    string PrizeName,
    string? PrizeImageUrl,
    decimal PrizeValue,
    string Currency,
    Guid? PrizeClaimId,
    PrizeClaimStatus? ClaimStatus);

public sealed class AuctionListQuery : PagedQuery
{
    public AuctionStatus? Status { get; set; }

    /// <summary>Convenience filter: live, upcoming, completed.</summary>
    public string? Group { get; set; }
}

public sealed record CreateAuctionRequest(
    string Title,
    string? Description,
    Guid MainProductId,
    decimal OriginalPrice,
    decimal StartingPrice,
    decimal? TargetPercentage,
    decimal? FinalPhasePercentage,
    decimal? BidIncrement,
    decimal? EntryFee,
    string? Currency,
    int? RequiredParticipants,
    int? StartDelayAfterParticipantsSeconds,
    int? DurationSeconds,
    ExpirationPolicy? ExpirationPolicy,
    IReadOnlyList<AuctionPrizeRequest> Prizes);

public sealed record AuctionPrizeRequest(Guid ProductId, int Rank);

public sealed record UpdateAuctionRequest(
    string? Title,
    string? Description,
    decimal? OriginalPrice,
    decimal? StartingPrice,
    decimal? TargetPercentage,
    decimal? FinalPhasePercentage,
    decimal? BidIncrement,
    decimal? EntryFee,
    int? RequiredParticipants,
    int? StartDelayAfterParticipantsSeconds,
    int? DurationSeconds,
    ExpirationPolicy? ExpirationPolicy,
    IReadOnlyList<AuctionPrizeRequest>? Prizes);

public sealed record UpdateScheduleRequest(DateTime ScheduledStartAt, string? Reason);

public sealed record PlaceBidRequest(string IdempotencyKey);

public sealed record JoinAuctionRequest(string? IdempotencyKey);
