namespace AuctionPlatform.Application.M5azn;

/// <summary>The order body of <c>POST /api/v1/orders</c>, as M5azn's documentation shows it.</summary>
public sealed record M5aznOrderRequest(
    long Id,
    decimal Total,
    string PaymentMethod,
    decimal CodAmount,
    DateTime OrderCreatedAt,
    string PlatformName,
    M5aznShippingInfo ShippingInfo,
    M5aznCustomerInfo CustomerInfo,
    IReadOnlyList<M5aznOrderItem> Items);

public sealed record M5aznShippingInfo(string Address, string Country, string CountryCode, string City);

public sealed record M5aznCustomerInfo(string FirstName, string LastName, string Email, string Phone);

public sealed record M5aznOrderItem(int Id, long ProductId, string Sku, string Name, int QtyOrdered);

/// <summary>What became of sending an order, judged by what it means for sending it again.</summary>
public enum M5aznSubmitOutcome
{
    /// <summary>M5azn took the order.</summary>
    Accepted,

    /// <summary>M5azn refused the order itself. Sending the same body again would be refused too.</summary>
    Rejected,

    /// <summary>The order certainly did not reach M5azn (no sign-in, no connection). Safe to send again.</summary>
    NotSent,

    /// <summary>No clear answer (a timeout, a server error). M5azn may or may not have the order.</summary>
    Unknown
}

public sealed record M5aznSubmitResult(
    M5aznSubmitOutcome Outcome,
    string? RemoteOrderId = null,
    string? RemoteStatus = null,
    string? Message = null);

/// <summary>What <c>GET /api/orders/{id}</c> says about an order. Every field is optional: none is documented.</summary>
public sealed record M5aznRemoteOrder(
    string? RemoteOrderId,
    string? Status,
    string? TrackingNumber,
    string? TrackingUrl,
    string? Carrier);

/// <summary>One line of <c>GET /api/v1/products/qty-changed</c>.</summary>
public sealed record M5aznStockChange(long ProductId, string? Sku, int Quantity);

/// <summary>
/// A product of the seller's M5azn list (<c>GET /api/v1/seller/myproductlist</c>) or its details
/// (<c>GET /api/v1/product/details/{id}</c>). Neither answer is documented, so every field but the id
/// is optional. <c>Price</c> is what M5azn charges the seller.
/// </summary>
public sealed record M5aznCatalogProduct(
    long ProductId,
    string? Sku,
    string? Name,
    string? Description,
    decimal? Price,
    int? Quantity,
    IReadOnlyList<string> Images,
    string? CategoryName,
    string? DeliveryTime,

    /// <summary>M5azn's id of the product's category, when the answer gives one but no name.</summary>
    long? CategoryId = null,

    /// <summary>The main category above <see cref="CategoryName"/>, when that is a sub-category.</summary>
    string? ParentCategoryName = null);

/// <summary>One category of M5azn's catalogue (<c>GET /api/v1/catalog/categories</c>), sub-categories flattened.</summary>
public sealed record M5aznCategory(long Id, string Name, long? ParentId, string? ImageUrl);

/// <summary>M5azn's categories. <c>Problem</c> says, in words, what could not be read.</summary>
public sealed record M5aznCategoryList(IReadOnlyList<M5aznCategory> Categories, string? Problem);

/// <summary>
/// One page of the seller's product list. <c>Problem</c> says what could not be read, in words.
/// <c>Unreadable</c>: the answer as a whole was not understood, so nothing may be concluded from it
/// (above all, not that the seller has no products).
/// </summary>
public sealed record M5aznProductPage(
    IReadOnlyList<M5aznCatalogProduct> Products, bool HasMore, string? Problem, bool Unreadable = false);

/// <summary>
/// M5azn's seller API. Implemented in Infrastructure with a typed HttpClient; the application only
/// sees answers already translated into these records.
/// </summary>
public interface IM5aznClient
{
    /// <summary>The feature flag is on and the seller account is configured.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Signs in afresh with the account in use, to check it. Null when it worked, else why not.
    /// Places nothing: sign-in is free. Works while switched off, so an account can be checked first.
    /// </summary>
    Task<string?> CheckSignInAsync(CancellationToken ct = default);

    Task<M5aznSubmitResult> CreateOrderAsync(M5aznOrderRequest order, CancellationToken ct = default);

    /// <summary>Null when M5azn does not know the order.</summary>
    Task<M5aznRemoteOrder?> GetOrderAsync(string orderId, CancellationToken ct = default);

    Task<IReadOnlyList<M5aznStockChange>> GetQuantityChangesAsync(int hours, CancellationToken ct = default);

    Task<M5aznCatalogProduct?> GetProductAsync(long productId, CancellationToken ct = default);

    /// <summary>
    /// One page (from 1) of the products the seller has added to their M5azn shop, or only those of
    /// one category. Read-only.
    /// </summary>
    Task<M5aznProductPage> GetMyProductsAsync(int page, long? categoryId = null, CancellationToken ct = default);

    /// <summary>M5azn's catalogue categories, sub-categories included. Read-only.</summary>
    Task<M5aznCategoryList> GetCategoriesAsync(CancellationToken ct = default);
}
