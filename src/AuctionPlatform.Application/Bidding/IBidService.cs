using AuctionPlatform.Application.Auctions;

namespace AuctionPlatform.Application.Bidding;

public interface IBidService
{
    /// <summary>
    /// Places one bid. The server derives the previous price, the new price, the sequence number
    /// and the timestamp; the client supplies nothing but an idempotency key.
    /// </summary>
    Task<BidResultDto> PlaceBidAsync(
        Guid auctionId, Guid userId, string idempotencyKey, CancellationToken ct = default);
}

/// <summary>
/// Finishing an auction: ranking, winner declaration, prize claims and entry-fee refunds.
/// Separated from bidding so it can also be driven by the expiry worker.
/// </summary>
public interface IAuctionCompletionService
{
    /// <summary>
    /// Moves a finished auction through WINNERS_DECLARED and processes refunds. Safe to call more
    /// than once and from more than one instance: the work is guarded and each step is idempotent.
    /// </summary>
    Task CompleteAuctionAsync(Guid auctionId, CancellationToken ct = default);

    /// <summary>Applies the configured expiration policy to an auction whose duration elapsed.</summary>
    Task HandleExpiryAsync(Guid auctionId, CancellationToken ct = default);

    /// <summary>
    /// Returns every entry fee paid into a cancelled auction as cash in the Main wallet. Safe to
    /// call repeatedly; each participant is refunded exactly once.
    /// </summary>
    Task RefundCancelledAuctionAsync(Guid auctionId, CancellationToken ct = default);
}
