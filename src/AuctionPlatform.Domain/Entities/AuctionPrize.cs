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

/// <summary>
    /// Optional link to a shared catalog product. Prizes are now entered against the auction
    /// itself, so this stays null for anything created since; it is kept so auctions built from
    /// the old catalog still resolve their source.
    /// </summary>
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>1..4</summary>
    public int Rank { get; set; }

    /// <summary>
    /// The prize as it stands for this auction. These three fields are the record, not a cache of
    /// one: a prize belongs to its auction, so editing another auction can never change it, and a
    /// finished auction keeps the prize exactly as it was awarded.
    /// </summary>
    public decimal OriginalPrice { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;
    public string? ProductImageSnapshot { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
