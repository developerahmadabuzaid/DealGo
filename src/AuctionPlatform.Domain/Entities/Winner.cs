using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Domain.Entities;

/// <summary>One of the four ranked winners of a finished auction. UNIQUE(AuctionId, Rank).</summary>
public class Winner : Entity
{
    public Guid AuctionId { get; set; }
    public Auction? Auction { get; set; }

    public Guid ParticipantId { get; set; }
    public AuctionParticipant? Participant { get; set; }

    public Guid UserId { get; set; }

    /// <summary>1..4</summary>
    public int Rank { get; set; }

    public Guid PrizeId { get; set; }
    public AuctionPrize? Prize { get; set; }

    public decimal HighestPrice { get; set; }
    public DateTime? HighestPriceAt { get; set; }
    public DateTime RankedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Convenience pointer to the claim. Deliberately not a foreign key: PrizeClaim.WinnerId is the
    /// owning side, and a second FK in the opposite direction would make the two rows uninsertable
    /// in any order.
    /// </summary>
    public Guid? PrizeClaimId { get; set; }
}

/// <summary>
/// A winner's claim on their prize. Winning does not mean the prize value has been paid;
/// the claim carries the payment the winner still owes.
/// </summary>
public class PrizeClaim : Entity
{
    public Guid WinnerId { get; set; }
    public Winner? Winner { get; set; }

    public Guid UserId { get; set; }
    public Guid AuctionId { get; set; }

    public Guid PrizeId { get; set; }
    public AuctionPrize? Prize { get; set; }

    public PrizeClaimStatus Status { get; set; } = PrizeClaimStatus.Pending;

    public decimal PaymentAmount { get; set; }
    public string Currency { get; set; } = Money.DefaultCurrency;

    public Guid? PaymentId { get; set; }
    public Guid? ShippingAddressId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PaymentRequiredAt { get; set; }
    public DateTime? PaymentDeadlineAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? FulfilledAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? ExpiredAt { get; set; }

    /// <summary>Optimistic concurrency token mapped to the PostgreSQL xmin system column.</summary>
    public uint Version { get; set; }
}
