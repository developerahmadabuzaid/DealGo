using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Application.Store;

public sealed record StoreCategoryDto(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    string? ImageUrl,
    int DisplayOrder,
    StoreCategoryStatus Status,
    int ProductCount);

public sealed record StoreProductImageDto(Guid Id, string Url, string? ThumbnailUrl, bool IsPrimary, int DisplayOrder);

public sealed record StoreProductListItemDto(
    Guid Id,
    string Name,
    string Slug,
    string Sku,
    Guid? CategoryId,
    string? CategoryName,
    decimal Price,
    string Currency,
    string? PrimaryImageUrl,
    int AvailableQuantity,
    bool InStock,
    StoreProductStatus Status,
    StorePaymentMode PaymentMode,
    bool AcceptsShoppingCredit,
    bool IsFeatured);

public sealed record StoreProductDetailDto(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    string Sku,
    Guid? CategoryId,
    string? CategoryName,
    decimal Price,
    string Currency,
    int StockQuantity,
    int ReservedQuantity,
    int AvailableQuantity,
    int SoldQuantity,
    bool InStock,
    StoreProductStatus Status,
    StorePaymentMode PaymentMode,
    bool AcceptsShoppingCredit,
    bool AcceptsMainWallet,
    bool IsFeatured,
    bool RequiresShipping,
    decimal ShippingFee,
    IReadOnlyList<StoreProductImageDto> Images,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed class StoreProductQuery : PagedQuery
{
    public Guid? CategoryId { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public bool? InStockOnly { get; set; }
    public bool? FeaturedOnly { get; set; }
    public StorePaymentMode? PaymentMode { get; set; }

    /// <summary>Admin-only filter; public queries always see the public visibility set.</summary>
    public StoreProductStatus? Status { get; set; }

    public bool? LowStockOnly { get; set; }
}

public sealed record StoreHomeDto(
    IReadOnlyList<StoreCategoryDto> Categories,
    IReadOnlyList<StoreProductListItemDto> Featured,
    IReadOnlyList<StoreProductListItemDto> NewArrivals,
    IReadOnlyList<StoreProductListItemDto> Popular,
    IReadOnlyList<StoreProductListItemDto> ShoppingCreditEligible,
    decimal ShoppingCreditBalance,
    string Currency);

public sealed record CreateStoreProductRequest(
    string Name,
    string? Slug,
    string? Description,
    Guid? CategoryId,
    string Sku,
    decimal Price,
    string? Currency,
    int StockQuantity,
    StoreProductStatus? Status,
    StorePaymentMode? PaymentMode,
    bool? IsFeatured,
    bool? RequiresShipping,
    decimal? ShippingFee,
    int? LowStockThreshold,
    IReadOnlyList<StoreProductImageRequest>? Images);

public sealed record StoreProductImageRequest(string Url, string? ThumbnailUrl, bool IsPrimary, int DisplayOrder);

public sealed record UpdateStoreProductRequest(
    string? Name,
    string? Slug,
    string? Description,
    Guid? CategoryId,
    string? Sku,
    decimal? Price,
    StorePaymentMode? PaymentMode,
    bool? IsFeatured,
    bool? RequiresShipping,
    decimal? ShippingFee,
    int? LowStockThreshold,
    IReadOnlyList<StoreProductImageRequest>? Images);

public sealed record CreateStoreCategoryRequest(
    string Name, string? Slug, string? Description, string? ImageUrl, int? DisplayOrder);

public sealed record UpdateStoreCategoryRequest(
    string? Name, string? Slug, string? Description, string? ImageUrl,
    int? DisplayOrder, StoreCategoryStatus? Status);

public sealed record AdjustInventoryRequest(int Delta, string Reason, InventoryTransactionType? Type);

public sealed record InventoryTransactionDto(
    Guid Id,
    Guid ProductId,
    string ProductName,
    InventoryTransactionType Type,
    int Quantity,
    int BeforeQuantity,
    int AfterQuantity,
    string? Reason,
    Guid? AdminUserId,
    DateTime CreatedAt);

public sealed record InventoryRowDto(
    Guid ProductId,
    string Name,
    string Sku,
    int StockQuantity,
    int ReservedQuantity,
    int AvailableQuantity,
    int SoldQuantity,
    int LowStockThreshold,
    bool IsLowStock,
    StoreProductStatus Status);

// ---- Orders ----

public sealed record CheckoutItemRequest(Guid ProductId, int Quantity);

public sealed record CheckoutRequest(
    IReadOnlyList<CheckoutItemRequest> Items,
    StorePaymentMethod PaymentMethod,
    Guid? ShippingAddressId,
    string IdempotencyKey);

public sealed record StoreOrderItemDto(
    Guid Id,
    Guid ProductId,
    string ProductName,
    string? ProductSku,
    string? ImageUrl,
    decimal UnitPrice,
    int Quantity,
    decimal TotalPrice);

public sealed record StoreOrderDto(
    Guid Id,
    string OrderNumber,
    Guid UserId,
    string? CustomerName,
    decimal Subtotal,
    decimal Discount,
    decimal ShippingFee,
    decimal Tax,
    decimal Total,
    string Currency,
    StorePaymentMethod PaymentMethod,
    StoreOrderStatus Status,
    ShippingAddressDto? ShippingAddress,
    string? TrackingNumber,
    IReadOnlyList<StoreOrderItemDto> Items,
    DateTime CreatedAt,
    DateTime? PaidAt,
    DateTime? ShippedAt,
    DateTime? DeliveredAt,
    DateTime? CompletedAt,
    DateTime? CancelledAt);

public sealed class StoreOrderQuery : PagedQuery
{
    public StoreOrderStatus? Status { get; set; }
    public StorePaymentMethod? PaymentMethod { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ProductId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public sealed record ShippingAddressDto(
    Guid Id, string FullName, string Phone, string Country, string City,
    string? Area, string AddressLine1, string? AddressLine2, string? PostalCode, bool IsDefault);

public sealed record SaveShippingAddressRequest(
    string FullName, string Phone, string Country, string City,
    string? Area, string AddressLine1, string? AddressLine2, string? PostalCode, bool IsDefault);

/// <summary>Server-calculated checkout totals. The client never computes an order total.</summary>
public sealed record CheckoutQuoteDto(
    decimal Subtotal,
    decimal Discount,
    decimal ShippingFee,
    decimal Tax,
    decimal Total,
    string Currency,
    decimal AvailableBalance,
    decimal RemainingAfterPurchase,
    bool CanAfford);

public sealed record StoreSettingsDto(
    bool StoreEnabled,
    bool ShoppingWalletEnabled,
    bool AllowShoppingWalletPurchases,
    bool AllowMainWalletPurchases,
    string DefaultCurrency,
    bool TaxEnabled,
    decimal TaxPercentage,
    bool ShippingEnabled,
    decimal DefaultShippingFee,
    decimal MinimumOrderAmount,
    decimal MaximumOrderAmount,
    bool ShoppingCreditExpirationEnabled,
    int ShoppingCreditExpirationDays,
    string? RefundPolicy,
    int LowStockThreshold);

public sealed record StoreOverviewDto(
    int TotalProducts,
    int ActiveProducts,
    int OutOfStockProducts,
    int LowStockProducts,
    long TotalOrders,
    long PendingOrders,
    long CompletedOrders,
    decimal StoreRevenue,
    decimal ShoppingCreditUsed,
    decimal ShoppingCreditIssued,
    decimal ShoppingCreditOutstanding,
    string Currency);
