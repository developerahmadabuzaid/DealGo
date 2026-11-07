namespace AuctionPlatform.Domain.Enums;

/// <summary>What an M5azn shipment fulfils. Store orders only: auctions and their prizes never go to M5azn.</summary>
public enum M5aznSourceType
{
    StoreOrder = 0
}

/// <summary>
/// Where an order sent to M5azn stands. Our own reading of it: M5azn's own status word is kept
/// next to it as it came (<c>RemoteStatus</c>), because its documentation does not list them.
/// </summary>
public enum M5aznShipmentStatus
{
    /// <summary>Found and ready, waiting for an administrator to send it (automatic sending is off).</summary>
    AwaitingApproval = 0,

    /// <summary>To be sent by the worker on its next pass.</summary>
    Queued = 1,

    /// <summary>M5azn accepted the order.</summary>
    Submitted = 2,

    /// <summary>M5azn is preparing it.</summary>
    Processing = 3,

    Shipped = 4,
    Delivered = 5,
    Cancelled = 6,

    /// <summary>Not sent: M5azn refused it, or every attempt failed. An administrator can send it again.</summary>
    Failed = 7
}
