using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace AuctionPlatform.Api.Hubs;

/// <summary>
/// SignalR implementation of the realtime contract. Every method here is invoked only after the
/// owning database transaction has committed, so a client never sees a state the database would
/// disagree with.
///
/// With the Redis backplane configured, a broadcast from any API instance reaches clients connected
/// to every other instance.
/// </summary>
public sealed class SignalRAuctionNotifier : IAuctionRealtimeNotifier
{
    private readonly IHubContext<AuctionHub> _hub;

    public SignalRAuctionNotifier(IHubContext<AuctionHub> hub) => _hub = hub;

    private IClientProxy Auction(Guid auctionId) => _hub.Clients.Group(AuctionHub.AuctionGroup(auctionId));
    private IClientProxy User(Guid userId) => _hub.Clients.Group(AuctionHub.UserGroup(userId));

    public Task ParticipantCountChangedAsync(Guid auctionId, int participantCount, int requiredParticipants, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("AuctionParticipantCountChanged",
            new { auctionId, participantCount, requiredParticipants }, ct);

    public Task AuctionReadyAsync(Guid auctionId, int participantCount, DateTime participantsCompletedAt, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("AuctionReady",
            new { auctionId, participantCount, participantsCompletedAt }, ct);

    public Task AuctionStartScheduledAsync(Guid auctionId, DateTime scheduledStartAt, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("AuctionStartScheduled",
            new { auctionId, scheduledStartAt, serverTime = DateTime.UtcNow }, ct);

    public Task AuctionStartTimeChangedAsync(Guid auctionId, DateTime? previousStartAt, DateTime newStartAt, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("AuctionStartTimeChanged",
            new { auctionId, previousStartAt, scheduledStartAt = newStartAt, serverTime = DateTime.UtcNow }, ct);

    public Task AuctionStartedAsync(Guid auctionId, DateTime startedAt, DateTime endsAt, decimal currentPrice, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("AuctionStarted",
            new { auctionId, startedAt, endsAt, currentPrice, serverTime = DateTime.UtcNow }, ct);

    public Task BidPlacedAsync(Guid auctionId, BidBroadcast bid, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("BidPlaced", bid, ct);

    public Task AuctionPriceChangedAsync(Guid auctionId, decimal currentPrice, decimal targetPrice, decimal progress, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("AuctionPriceChanged",
            new { auctionId, currentPrice, targetPrice, progress }, ct);

    public Task ParticipantRankChangedAsync(Guid auctionId, Guid userId, int rank, decimal highestPrice, CancellationToken ct = default) =>
        User(userId).SendAsync("ParticipantRankChanged",
            new { auctionId, rank, highestPrice }, ct);

    public Task LeaderboardUpdatedAsync(Guid auctionId, IReadOnlyList<LeaderboardEntryDto> leaderboard, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("LeaderboardUpdated",
            new { auctionId, leaderboard }, ct);

    public Task FinalPhaseStartedAsync(Guid auctionId, decimal currentPrice, decimal targetPrice, int? remainingSeconds, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("FinalPhaseStarted",
            new { auctionId, currentPrice, targetPrice, remainingSeconds, serverTime = DateTime.UtcNow }, ct);

    public Task AuctionFinishedAsync(Guid auctionId, decimal finalPrice, bool targetReached, DateTime finishedAt, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("AuctionFinished",
            new { auctionId, finalPrice, targetReached, finishedAt }, ct);

    public Task WinnersDeclaredAsync(Guid auctionId, IReadOnlyList<WinnerDto> winners, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("WinnersDeclared", new { auctionId, winners }, ct);

    public Task PrizeClaimAvailableAsync(Guid userId, Guid prizeClaimId, Guid auctionId, decimal paymentAmount, string currency, CancellationToken ct = default) =>
        User(userId).SendAsync("PrizeClaimAvailable",
            new { prizeClaimId, auctionId, paymentAmount, currency }, ct);

    public Task ShoppingCreditAddedAsync(Guid userId, decimal amount, decimal newBalance, string currency, Guid? auctionId, CancellationToken ct = default) =>
        User(userId).SendAsync("ShoppingCreditAdded",
            new { amount, newBalance, currency, auctionId }, ct);

    public Task AuctionCancelledAsync(Guid auctionId, string? reason, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("AuctionCancelled", new { auctionId, reason }, ct);

    public Task AuctionPausedAsync(Guid auctionId, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("AuctionPaused", new { auctionId }, ct);

    public Task AuctionResumedAsync(Guid auctionId, DateTime? newEndsAt, CancellationToken ct = default) =>
        Auction(auctionId).SendAsync("AuctionResumed",
            new { auctionId, endsAt = newEndsAt, serverTime = DateTime.UtcNow }, ct);
}
