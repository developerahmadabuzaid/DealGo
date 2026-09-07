using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Domain.Entities;

/// <summary>A user's registration in one auction. UNIQUE(AuctionId, UserId).</summary>
public class AuctionParticipant : Entity
{
    public Guid AuctionId { get; set; }
    public Auction? Auction { get; set; }

    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public Guid? EntryFeePaymentId { get; set; }
    public decimal EntryFeePaid { get; set; }
    public string Currency { get; set; } = Money.DefaultCurrency;

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The auction price this participant last pushed the auction to.</summary>
    public decimal CurrentPrice { get; set; }

    /// <summary>Highest price this participant reached: the primary ranking key.</summary>
    public decimal HighestPrice { get; set; }

    /// <summary>Server timestamp at which HighestPrice was first reached: the ranking tie-break.</summary>
    public DateTime? HighestPriceAt { get; set; }

    public int CurrentRank { get; set; }
    public int BidCount { get; set; }
    public DateTime? LastBidAt { get; set; }

    public ParticipantStatus Status { get; set; } = ParticipantStatus.Active;

    /// <summary>Optimistic concurrency token mapped to the PostgreSQL xmin system column.</summary>
    public uint Version { get; set; }

    public ICollection<Bid> Bids { get; set; } = new List<Bid>();
}
