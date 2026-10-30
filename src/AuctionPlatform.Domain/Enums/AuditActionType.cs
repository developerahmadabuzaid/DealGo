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
    PrizeClaimAdjusted = 22,

    /// <summary>Any edit to the public landing page's wording, cards, reviews or questions.</summary>
    SiteContentChanged = 23,

    /// <summary>An administrator put a user into an auction and charged them the entry fee.</summary>
    ParticipantAddedByAdmin = 24,

    /// <summary>An advertisement on the app's front page was added, changed or removed.</summary>
    AppAdChanged = 25,

    /// <summary>A user deleted their own account. Their personal details were removed.</summary>
    AccountDeleted = 26,

    /// <summary>Staff marked a withdrawal as paid after making the bank transfer.</summary>
    WithdrawalPaid = 27,

    /// <summary>Staff refused a withdrawal; the amount went back to the user's main wallet.</summary>
    WithdrawalRejected = 28
}
