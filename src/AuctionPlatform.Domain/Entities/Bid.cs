using AuctionPlatform.Domain.Common;

namespace AuctionPlatform.Domain.Entities;

/// <summary>
/// An accepted bid. Immutable once written.
/// UNIQUE(AuctionId, SequenceNumber) and UNIQUE(AuctionId, ParticipantId, IdempotencyKey).
/// </summary>
public class Bid : Entity
{
    public Guid AuctionId { get; set; }
    public Auction? Auction { get; set; }

    public Guid ParticipantId { get; set; }
    public AuctionParticipant? Participant { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Monotonic per-auction ordinal assigned by the server inside the bid transaction.</summary>
    public long SequenceNumber { get; set; }

    public decimal PreviousPrice { get; set; }
    public decimal NewPrice { get; set; }

    /// <summary>Client-generated key that makes a retried bid request return the original result.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>True when this bid is the one that reached the target price.</summary>
    public bool ReachedTarget { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Authoritative server clock reading. Client timestamps are never trusted.</summary>
    public DateTime ServerTimestamp { get; set; } = DateTime.UtcNow;
}
