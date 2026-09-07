using AuctionPlatform.Domain.Common;

namespace AuctionPlatform.Domain.Entities;

/// <summary>
/// One of exactly four prizes on a published auction. Rank 1 is the main auction product.
/// UNIQUE(AuctionId, Rank).
/// </summary>
public class AuctionPrize : Entity
{
    public Guid AuctionId { get; set; }
    public Auction? Auction { get; set; }

    public Guid ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>1..4</summary>
    public int Rank { get; set; }

    /// <summary>Snapshot at publish time so later catalog edits do not rewrite history.</summary>
    public decimal OriginalPrice { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;
    public string? ProductImageSnapshot { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
