using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Domain.Entities;

public class StoreCategory : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public int DisplayOrder { get; set; }
    public StoreCategoryStatus Status { get; set; } = StoreCategoryStatus.Active;

    /// <summary>
    /// The main department this one sits under, or null for a main department. Two levels only:
    /// a department with a parent cannot have children of its own.
    /// </summary>
    public Guid? ParentId { get; set; }
    public StoreCategory? Parent { get; set; }
    public ICollection<StoreCategory> Children { get; set; } = new List<StoreCategory>();

    public ICollection<StoreProduct> Products { get; set; } = new List<StoreProduct>();
}

/// <summary>A sellable shop item. Archived rather than deleted once referenced by an order.</summary>
public class StoreProduct : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }

    public Guid? CategoryId { get; set; }
    public StoreCategory? Category { get; set; }

    /// <summary>Unique stock-keeping unit, e.g. SKU-HEADPHONES-001.</summary>
    public string Sku { get; set; } = string.Empty;

    public decimal Price { get; set; }

    /// <summary>
    /// What the item costs without the offer. Null, or anything not above <see cref="Price"/>,
    /// means there is no offer: a discount is only ever the difference between the two, never a
    /// number typed in on its own.
    /// </summary>
    public decimal? CompareAtPrice { get; set; }

    public string Currency { get; set; } = Money.DefaultCurrency;

    /// <summary>Physical units on hand, including any currently reserved by open orders.</summary>
    public int StockQuantity { get; set; }

    /// <summary>Units committed to orders that are paid but not yet fulfilled.</summary>
    public int ReservedQuantity { get; set; }

    public int SoldQuantity { get; set; }
    public int LowStockThreshold { get; set; } = 5;

    public StoreProductStatus Status { get; set; } = StoreProductStatus.Draft;
    public StorePaymentMode PaymentMode { get; set; } = StorePaymentMode.ShoppingWallet;

    public bool IsFeatured { get; set; }
    public bool RequiresShipping { get; set; } = true;
    public decimal ShippingFee { get; set; }

    public int ViewCount { get; set; }

    /// <summary>Optimistic concurrency token mapped to the PostgreSQL xmin system column.</summary>
    public uint Version { get; set; }

    public ICollection<StoreProductImage> Images { get; set; } = new List<StoreProductImage>();

    public int AvailableQuantity => StockQuantity - ReservedQuantity;

    public bool IsOnOffer => CompareAtPrice is { } was && was > Price;

    /// <summary>Whole percent off, rounded down, or zero when there is no offer.</summary>
    public int DiscountPercentage =>
        IsOnOffer ? (int)Math.Floor((CompareAtPrice!.Value - Price) / CompareAtPrice.Value * 100m) : 0;

    public bool IsPubliclyVisible =>
        Status == StoreProductStatus.Active || Status == StoreProductStatus.OutOfStock;
}

public class StoreProductImage : Entity
{
    public Guid ProductId { get; set; }
    public StoreProduct? Product { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public bool IsPrimary { get; set; }
    public int DisplayOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Append-only stock movement. Every change to StockQuantity writes one of these.</summary>
public class InventoryTransaction : Entity
{
    public Guid ProductId { get; set; }
    public StoreProduct? Product { get; set; }

    public InventoryTransactionType Type { get; set; }

    /// <summary>Signed delta applied to stock.</summary>
    public int Quantity { get; set; }

    public int BeforeQuantity { get; set; }
    public int AfterQuantity { get; set; }

    public string? ReferenceType { get; set; }
    public Guid? ReferenceId { get; set; }

    public string? Reason { get; set; }
    public Guid? AdminUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class StoreOrder : Entity
{
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>Human friendly identifier, e.g. ORD-2026-000001.</summary>
    public string OrderNumber { get; set; } = string.Empty;

    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal ShippingFee { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = Money.DefaultCurrency;

    public StorePaymentMethod PaymentMethod { get; set; } = StorePaymentMethod.ShoppingWallet;
    public StoreOrderStatus Status { get; set; } = StoreOrderStatus.Pending;

    public Guid? ShippingAddressId { get; set; }
    public ShippingAddress? ShippingAddress { get; set; }

    public Guid? WalletTransactionId { get; set; }
    public Guid? RefundWalletTransactionId { get; set; }

    /// <summary>Checkout idempotency key. UNIQUE(UserId, IdempotencyKey) when present.</summary>
    public string? IdempotencyKey { get; set; }

    public string? TrackingNumber { get; set; }
    public string? CancellationReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; set; }
    public DateTime? ProcessingAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? RefundedAt { get; set; }

    /// <summary>Optimistic concurrency token mapped to the PostgreSQL xmin system column.</summary>
    public uint Version { get; set; }

    public ICollection<StoreOrderItem> Items { get; set; } = new List<StoreOrderItem>();
}

/// <summary>
/// A line on an order. Name and unit price are snapshots, so changing a product's price later
/// never rewrites an existing order.
/// </summary>
public class StoreOrderItem : Entity
{
    public Guid OrderId { get; set; }
    public StoreOrder? Order { get; set; }

    public Guid ProductId { get; set; }
    public StoreProduct? Product { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;
    public string? ProductSkuSnapshot { get; set; }
    public string? ProductImageSnapshot { get; set; }

    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal TotalPrice { get; set; }
}

public class ShippingAddress : Entity
{
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string? Area { get; set; }
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string? PostalCode { get; set; }
    public bool IsDefault { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
