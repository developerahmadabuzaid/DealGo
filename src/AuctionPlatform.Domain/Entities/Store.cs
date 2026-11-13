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

    /// <summary>
    /// An exclusive offer ("عرض حصري"): a product of its own, shown only in the exclusive offers
    /// section, sold only between <see cref="OfferStartsAt"/> and <see cref="OfferEndsAt"/>, and at
    /// most <see cref="OfferMaxPerUser"/> to each customer across all their orders. Its stock is how
    /// many are offered in all; its compare-at price is the price before the offer.
    /// </summary>
    public bool IsExclusiveOffer { get; set; }
    public DateTime? OfferStartsAt { get; set; }
    public DateTime? OfferEndsAt { get; set; }
    public int? OfferMaxPerUser { get; set; }

    public bool IsOfferLive(DateTime now) =>
        IsExclusiveOffer && OfferStartsAt is { } s && OfferEndsAt is { } e && s <= now && now < e;

    /// <summary>
    /// Admin: added in the dashboard and shipped by DealGo. M5azn: imported from the M5azn seller
    /// account (docs/M5AZN.md); only these are ever sent to M5azn.
    /// </summary>
    public StoreProductSource Source { get; set; } = StoreProductSource.Admin;

    /// <summary>How long delivery takes, as shown to the customer ("1-5 أيام"). Null: not shown.</summary>
    public string? DeliveryTime { get; set; }

    /// <summary>
    /// The M5azn product this item is, for products imported from M5azn. Null for everything DealGo
    /// ships itself. Paid lines are sent to M5azn, and stock and price follow M5azn's.
    /// </summary>
    public long? M5aznProductId { get; set; }

    /// <summary>M5azn's SKU for the item (the variant, such as size or colour), sent with each order.</summary>
    public string? M5aznSku { get; set; }

    /// <summary>
    /// What M5azn charges for the item, as last read from M5azn. The DealGo price is this plus the
    /// profit percentage set on the M5azn page. Null until M5azn has been asked.
    /// </summary>
    public decimal? M5aznCostPrice { get; set; }

    /// <summary>
    /// A profit percentage for this product alone, set by staff on the M5azn page. Null: the general
    /// profit percentage of the M5azn settings applies.
    /// </summary>
    public decimal? M5aznProfitPercentage { get; set; }

    /// <summary>When the last import found the product in the M5azn seller list.</summary>
    public DateTime? M5aznSyncedAt { get; set; }

    /// <summary>
    /// Set when an import no longer finds the product in the M5azn seller list, and the product was
    /// hidden for it. Cleared, and the product shown again, if a later import finds it.
    /// </summary>
    public DateTime? M5aznRemovedAt { get; set; }

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

/// <summary>
/// Where a store order or a won prize is delivered. Laid out the Saudi way: city, district,
/// street and building, with the national short address and a pin on the map when the customer
/// gives them.
/// </summary>
public class ShippingAddress : Entity
{
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>The recipient, who may not be the account holder.</summary>
    public string FullName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string? District { get; set; }
    public string Street { get; set; } = string.Empty;
    public string? BuildingNumber { get; set; }

    /// <summary>Directions for the courier: a landmark, the floor, the gate to use.</summary>
    public string? Notes { get; set; }

    /// <summary>The Saudi National Address short code: four letters and four digits, e.g. RRRD2929.</summary>
    public string? ShortAddress { get; set; }

    /// <summary>No longer collected; kept so addresses saved before the Saudi layout keep their code.</summary>
    public string? PostalCode { get; set; }

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>
    /// Set when the customer removes or rewrites an address that an order or a prize was already
    /// sent to. The row stays, so what was delivered where never changes; it just stops being offered.
    /// </summary>
    public DateTime? ArchivedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
