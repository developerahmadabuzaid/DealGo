using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Domain.Entities;

/// <summary>
/// One order handed to M5azn, the dropshipping supplier that packs and delivers products from its
/// own warehouse (docs/M5AZN.md). It fulfils the M5azn-linked lines of one store order; auctions
/// and their prizes never go to M5azn. UNIQUE(SourceType, SourceId): an order is never sent twice.
/// </summary>
public class M5aznShipment : Entity
{
    /// <summary>
    /// The number sent to M5azn as the order's <c>id</c>. Its example uses a number, and ours are
    /// GUIDs, so each shipment takes the next one from a database sequence.
    /// </summary>
    public long Reference { get; set; }

    public M5aznSourceType SourceType { get; set; }
    public Guid SourceId { get; set; }

    /// <summary>The customer, so their own screens can find it without going through the source.</summary>
    public Guid UserId { get; set; }

    public M5aznShipmentStatus Status { get; set; } = M5aznShipmentStatus.Queued;

    /// <summary>M5azn's own id for the order, when its answer gives one.</summary>
    public string? RemoteOrderId { get; set; }

    /// <summary>M5azn's status word, exactly as it came.</summary>
    public string? RemoteStatus { get; set; }

    public string? TrackingNumber { get; set; }
    public string? TrackingUrl { get; set; }
    public string? Carrier { get; set; }

    /// <summary>Why the last attempt failed, in words an administrator can act on.</summary>
    public string? LastError { get; set; }

    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>The order body sent, kept for support. It holds the customer's address, never credentials.</summary>
    public string? RequestJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    /// <summary>Optimistic concurrency token mapped to the PostgreSQL xmin system column.</summary>
    public uint Version { get; set; }

    /// <summary>Still moving at M5azn, so its status is worth asking about.</summary>
    public bool IsInFlight => Status is M5aznShipmentStatus.Submitted
        or M5aznShipmentStatus.Processing
        or M5aznShipmentStatus.Shipped;
}
