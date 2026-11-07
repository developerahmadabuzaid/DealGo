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
/// The few fields of <c>GET /api/v1/product/details/{id}</c> an administrator needs to link a product.
/// <c>Price</c> is what M5azn charges the seller (its seller or wholesale price where it gives one).
/// </summary>
public sealed record M5aznProductInfo(long ProductId, string? Name, string? Sku, decimal? Price, int? Quantity, string? ImageUrl);

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

    Task<M5aznProductInfo?> GetProductAsync(long productId, CancellationToken ct = default);
}
