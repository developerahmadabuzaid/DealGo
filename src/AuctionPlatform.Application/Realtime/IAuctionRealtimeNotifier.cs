using AuctionPlatform.Application.Auctions;

namespace AuctionPlatform.Application.Realtime;

/// <summary>
/// Fan-out of authoritative state changes to connected clients. SignalR is a delivery mechanism,
/// never a source of truth, so every one of these is called strictly <b>after</b> the owning
/// database transaction has committed.
/// </summary>
public interface IAuctionRealtimeNotifier
{
    Task ParticipantCountChangedAsync(Guid auctionId, int participantCount, int requiredParticipants, CancellationToken ct = default);
    Task AuctionReadyAsync(Guid auctionId, int participantCount, DateTime participantsCompletedAt, CancellationToken ct = default);
    Task AuctionStartScheduledAsync(Guid auctionId, DateTime scheduledStartAt, CancellationToken ct = default);
    Task AuctionStartTimeChangedAsync(Guid auctionId, DateTime? previousStartAt, DateTime newStartAt, CancellationToken ct = default);
    Task AuctionStartedAsync(Guid auctionId, DateTime startedAt, DateTime endsAt, decimal currentPrice, CancellationToken ct = default);

    Task BidPlacedAsync(Guid auctionId, BidBroadcast bid, CancellationToken ct = default);
    Task AuctionPriceChangedAsync(Guid auctionId, decimal currentPrice, decimal targetPrice, decimal progress, CancellationToken ct = default);
    Task ParticipantRankChangedAsync(Guid auctionId, Guid userId, int rank, decimal highestPrice, CancellationToken ct = default);
    Task LeaderboardUpdatedAsync(Guid auctionId, IReadOnlyList<LeaderboardEntryDto> leaderboard, CancellationToken ct = default);

    Task FinalPhaseStartedAsync(Guid auctionId, decimal currentPrice, decimal targetPrice, int? remainingSeconds, CancellationToken ct = default);

    Task AuctionFinishedAsync(Guid auctionId, decimal finalPrice, bool targetReached, DateTime finishedAt, CancellationToken ct = default);
    Task WinnersDeclaredAsync(Guid auctionId, IReadOnlyList<WinnerDto> winners, CancellationToken ct = default);

    Task PrizeClaimAvailableAsync(Guid userId, Guid prizeClaimId, Guid auctionId, decimal paymentAmount, string currency, CancellationToken ct = default);
    Task ShoppingCreditAddedAsync(Guid userId, decimal amount, decimal newBalance, string currency, Guid? auctionId, CancellationToken ct = default);

    Task AuctionCancelledAsync(Guid auctionId, string? reason, CancellationToken ct = default);
    Task AuctionPausedAsync(Guid auctionId, CancellationToken ct = default);
    Task AuctionResumedAsync(Guid auctionId, DateTime? newEndsAt, CancellationToken ct = default);
}

/// <summary>Everything a client needs to render a bid without re-querying.</summary>
public sealed record BidBroadcast(
    Guid BidId,
    long SequenceNumber,
    Guid ParticipantId,
    string DisplayName,
    decimal PreviousPrice,
    decimal NewPrice,
    decimal TargetPrice,
    decimal Progress,
    bool ReachedTarget,
    DateTime ServerTimestamp);
