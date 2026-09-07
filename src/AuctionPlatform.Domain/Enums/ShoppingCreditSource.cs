namespace AuctionPlatform.Domain.Enums;

/// <summary>Every shopping-wallet credit must be attributable to a source for accounting.</summary>
public enum ShoppingCreditSource
{
    AuctionEntryFeeRefund = 0,
    AdminAdjustment = 1,
    StoreRefund = 2,
    Promotion = 3,
    Bonus = 4
}
