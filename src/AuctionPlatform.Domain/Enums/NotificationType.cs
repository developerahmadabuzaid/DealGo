namespace AuctionPlatform.Domain.Enums;

public enum NotificationType
{
    RequiredParticipantsReached = 0,
    AuctionStartScheduled = 1,
    AuctionStartTimeChanged = 2,
    AuctionStarted = 3,
    FinalPhaseStarted = 4,
    AuctionFinished = 5,
    YouWon = 6,
    YouLost = 7,
    ShoppingCreditAdded = 8,
    PrizeClaimAvailable = 9,
    PrizePaymentReminder = 10,
    StoreOrderCreated = 11,
    OrderShipped = 12,
    OrderDelivered = 13,
    LowStock = 14,
    ShoppingCreditExpiring = 15,
    Generic = 16,
    EntryFeeRefunded = 17,

    /// <summary>A message staff wrote in the dashboard and sent to everyone.</summary>
    AdminMessage = 18,

    /// <summary>An auction was published and is open for registration.</summary>
    NewAuction = 19,

    /// <summary>A winner's time to pay for their device ran out; the entry fee went to their Shopping wallet.</summary>
    PrizeClaimExpired = 20
}
