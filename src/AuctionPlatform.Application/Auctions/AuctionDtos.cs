using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Application.Auctions;

public sealed record AuctionPrizeDto(
    Guid Id,
    Guid? ProductId,
    int Rank,
    string ProductName,
    string? ImageUrl,
    string? Description,
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
    int? RemainingSeconds,
    string? CoverImageUrl,
    bool IsFeatured,
    string? WinnerName = null,
    DateTime? FinishedAt = null,
    // Whether the signed-in user is a participant, so a list can mark the auctions they are in.
    // Always false for an anonymous request.
    bool HasJoined = false,
    // The devices the auction awards, by place, so a list can show them under each auction.
    IReadOnlyList<AuctionListPrizeDto>? Prizes = null);

/// <summary>One device an auction awards, as a list shows it: its place, name and picture.</summary>
public sealed record AuctionListPrizeDto(int Rank, string Name, string? ImageUrl, decimal OriginalPrice);

/// <summary>One accepted bid, as the public bid log of an auction shows it. Names are masked.</summary>
public sealed record BidHistoryEntryDto(
    long SequenceNumber,
    decimal Price,
    string DisplayName,
    DateTime PlacedAt,
    bool IsMe);

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
    DateTime ServerTime,
    string? CoverImageUrl,
    bool IsFeatured,
    decimal StartingPercentage);

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
    DateTime ServerTime,
    Guid? MyParticipantId = null,
    Guid? LastBidParticipantId = null);

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
    PrizeClaimStatus? ClaimStatus,
    // What the winner pays: PrizeValue with the auction's discount applied. PrizeValue is the
    // device's full price, shown crossed out beside it.
    decimal PaymentAmount);

public sealed class AuctionListQuery : PagedQuery
{
    public AuctionStatus? Status { get; set; }

    /// <summary>Convenience filter: live, upcoming, completed.</summary>
    public string? Group { get; set; }

    /// <summary>Only the auctions an administrator picked for the front page.</summary>
    public bool? Featured { get; set; }
}

/// <summary>
/// A new auction and the four prizes that belong to it.
///
/// There is no OriginalPrice field: the auction is worth what its first prize is worth, so that
/// figure is taken from the rank 1 prize. Letting an administrator type it separately allowed an
/// auction to advertise one value while awarding another.
/// </summary>
public sealed record CreateAuctionRequest(
    string Title,
    string? Description,
    // A share of the target price; the starting price is worked out from it. Null means the default.
    decimal? StartingPercentage,
    decimal? TargetPercentage,
    decimal? FinalPhasePercentage,
    decimal? BidIncrement,
    decimal? EntryFee,
    string? Currency,
    int? RequiredParticipants,
    int? StartDelayAfterParticipantsSeconds,
    int? DurationSeconds,
    ExpirationPolicy? ExpirationPolicy,
    IReadOnlyList<AuctionPrizeRequest> Prizes,
    string? CoverImageUrl = null);

/// <summary>
/// One prize, described in full. Prizes belong to their auction: two auctions may award the same
/// thing and still hold their own name, price and picture for it.
/// </summary>
public sealed record AuctionPrizeRequest(
    int Rank,
    string Name,
    decimal Price,
    string? ImageUrl,
    string? Description);

public sealed record UpdateAuctionRequest(
    string? Title,
    string? Description,
    decimal? StartingPercentage,
    decimal? TargetPercentage,
    decimal? FinalPhasePercentage,
    decimal? BidIncrement,
    decimal? EntryFee,
    int? RequiredParticipants,
    int? StartDelayAfterParticipantsSeconds,
    int? DurationSeconds,
    ExpirationPolicy? ExpirationPolicy,
    IReadOnlyList<AuctionPrizeRequest>? Prizes,
    // Null leaves the picture as it is; an empty string removes it.
    string? CoverImageUrl = null);

public sealed record UpdateScheduleRequest(DateTime ScheduledStartAt, string? Reason);

public sealed record PlaceBidRequest(string IdempotencyKey);

public sealed record JoinAuctionRequest(string? IdempotencyKey);

/// <summary>An administrator adding a user to an auction, at an entry fee of their choosing.</summary>
public sealed record AddParticipantRequest(Guid UserId, decimal EntryFee);
