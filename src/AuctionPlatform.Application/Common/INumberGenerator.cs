namespace AuctionPlatform.Application.Common;

/// <summary>
/// Produces the human-friendly identifiers shown to customers and admins. Backed by database
/// sequences so numbers stay unique across API instances; the primary key remains the internal id.
/// </summary>
public interface INumberGenerator
{
    /// <summary>e.g. AUC-2026-000001</summary>
    Task<string> NextAuctionNumberAsync(CancellationToken ct = default);

    /// <summary>e.g. ORD-2026-000001</summary>
    Task<string> NextOrderNumberAsync(CancellationToken ct = default);
}
