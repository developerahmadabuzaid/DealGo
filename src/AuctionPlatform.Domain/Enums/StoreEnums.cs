namespace AuctionPlatform.Domain.Enums;

public enum StoreProductStatus
{
    Draft = 0,
    Active = 1,
    OutOfStock = 2,
    Hidden = 3,
    Archived = 4
}

public enum StoreCategoryStatus
{
    Active = 0,
    Hidden = 1,
    Archived = 2
}

/// <summary>Which wallet(s) a store product accepts.</summary>
public enum StorePaymentMode
{
    ShoppingWallet = 0,
    MainWallet = 1,
    Both = 2
}

public enum StorePaymentMethod
{
    ShoppingWallet = 0,
    MainWallet = 1
}

public enum StoreOrderStatus
{
    Pending = 0,
    Paid = 1,
    Processing = 2,
    Shipped = 3,
    Delivered = 4,
    Completed = 5,
    Cancelled = 6,
    Refunded = 7
}

public enum InventoryTransactionType
{
    Initial = 0,
    Purchase = 1,
    Adjustment = 2,
    Restock = 3,
    Return = 4,
    Cancellation = 5
}
