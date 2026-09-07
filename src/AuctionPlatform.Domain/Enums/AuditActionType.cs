namespace AuctionPlatform.Domain.Enums;

/// <summary>Administrative actions that must always leave an audit trail.</summary>
public enum AuditActionType
{
    StoreProductCreated = 0,
    StoreProductUpdated = 1,
    StoreProductArchived = 2,
    StoreCategoryCreated = 3,
    StoreCategoryUpdated = 4,
    InventoryAdjusted = 5,
    StoreOrderCreated = 6,
    StoreOrderStatusChanged = 7,
    StoreOrderCancelled = 8,
    StoreOrderRefunded = 9,
    ShoppingCreditIssued = 10,
    WalletAdjusted = 11,
    AuctionCreated = 12,
    AuctionUpdated = 13,
    AuctionPublished = 14,
    AuctionStarted = 15,
    AuctionScheduleChanged = 16,
    AuctionCancelled = 17,
    AuctionPaused = 18,
    AuctionResumed = 19,
    UserRoleChanged = 20,
    StoreSettingsChanged = 21,
    PrizeClaimAdjusted = 22
}
