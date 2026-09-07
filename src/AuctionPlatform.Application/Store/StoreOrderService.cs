using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionPlatform.Application.Store;

/// <summary>
/// Checkout and customer order management.
///
/// Checkout is a single transaction that locks every product row it touches (in id order, so
/// concurrent carts cannot deadlock), re-reads stock under that lock, debits the wallet and writes
/// the order. Two buyers racing for the last unit therefore produce one order and one OUT_OF_STOCK.
/// </summary>
public sealed class StoreOrderService : IStoreOrderService
{
    private readonly IApplicationDbContext _db;
    private readonly IWalletService _wallets;
    private readonly IDateTimeProvider _clock;
    private readonly INumberGenerator _numbers;
    private readonly INotificationService _notifications;
    private readonly ILogger<StoreOrderService> _logger;

    public StoreOrderService(
        IApplicationDbContext db,
        IWalletService wallets,
        IDateTimeProvider clock,
        INumberGenerator numbers,
        INotificationService notifications,
        ILogger<StoreOrderService> logger)
    {
        _db = db;
        _wallets = wallets;
        _clock = clock;
        _numbers = numbers;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<CheckoutQuoteDto> QuoteAsync(Guid userId, CheckoutRequest request, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        var products = await LoadProductsAsync(request, ct);
        var totals = CalculateTotals(request, products, settings);

        var wallets = await _wallets.GetWalletsAsync(userId, ct);
        var balance = request.PaymentMethod == StorePaymentMethod.MainWallet
            ? wallets.Main.Balance
            : wallets.Shopping.Balance;

        return new CheckoutQuoteDto(
            totals.Subtotal, totals.Discount, totals.ShippingFee, totals.Tax, totals.Total,
            settings.DefaultCurrency, balance,
            Money.Round(balance - totals.Total), balance >= totals.Total);
    }

    public async Task<StoreOrderDto> CheckoutAsync(Guid userId, CheckoutRequest request, CancellationToken ct = default)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The cart is empty.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "An idempotency key is required.");
        }

        // A retried checkout returns the original order rather than buying twice.
        var existing = await _db.StoreOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.UserId == userId && o.IdempotencyKey == request.IdempotencyKey, ct);

        if (existing is not null)
        {
            return await GetOrderAsync(userId, existing.Id, ct);
        }

        var settings = await GetSettingsAsync(ct);

        if (!settings.StoreEnabled)
        {
            throw new DomainException(ErrorCodes.StoreDisabled, "The store is currently unavailable.");
        }

        if (request.PaymentMethod == StorePaymentMethod.ShoppingWallet && !settings.AllowShoppingWalletPurchases)
        {
            throw new DomainException(
                ErrorCodes.PaymentMethodNotAccepted, "Shopping wallet purchases are disabled.");
        }

        if (request.PaymentMethod == StorePaymentMethod.MainWallet && !settings.AllowMainWalletPurchases)
        {
            throw new DomainException(
                ErrorCodes.PaymentMethodNotAccepted, "Main wallet purchases are disabled.");
        }

        Guid orderId;

        await using (var tx = await _db.BeginTransactionAsync(ct))
        {
            var now = _clock.UtcNow;

            // Deterministic lock order across all carts prevents deadlocks between concurrent buyers.
            var productIds = request.Items.Select(i => i.ProductId).Distinct().OrderBy(id => id).ToArray();

            var products = await _db.StoreProducts
                .FromSqlRaw(
                    """
                    SELECT *, xmin FROM "StoreProducts"
                    WHERE "Id" = ANY({0})
                    ORDER BY "Id"
                    FOR UPDATE
                    """,
                    productIds)
                .ToListAsync(ct);

            var byId = products.ToDictionary(p => p.Id);

            foreach (var item in request.Items)
            {
                if (item.Quantity <= 0)
                {
                    throw new DomainException(ErrorCodes.ValidationFailed, "Quantity must be at least 1.");
                }

                if (!byId.TryGetValue(item.ProductId, out var product))
                {
                    throw new DomainException(ErrorCodes.ProductNotFound, $"Product {item.ProductId} was not found.");
                }

                if (product.Status != StoreProductStatus.Active)
                {
                    throw new DomainException(
                        ErrorCodes.ProductNotAvailable, $"{product.Name} is not available for purchase.");
                }

                if (!WalletRules.AcceptsPayment(product.PaymentMode, request.PaymentMethod))
                {
                    throw new DomainException(
                        ErrorCodes.PaymentMethodNotAccepted,
                        $"{product.Name} cannot be paid for with this wallet.");
                }

                // Re-read under the lock: the stock we validate is the stock we decrement.
                if (product.AvailableQuantity < item.Quantity)
                {
                    throw new DomainException(
                        ErrorCodes.OutOfStock,
                        $"{product.Name} does not have enough stock ({product.AvailableQuantity} available).");
                }
            }

            var totals = CalculateTotals(request, products, settings);

            if (totals.Total < settings.MinimumOrderAmount || totals.Total > settings.MaximumOrderAmount)
            {
                throw new DomainException(
                    ErrorCodes.OrderAmountOutOfRange,
                    $"Order total must be between {settings.MinimumOrderAmount} and {settings.MaximumOrderAmount}.");
            }

            var order = new StoreOrder
            {
                UserId = userId,
                OrderNumber = await _numbers.NextOrderNumberAsync(ct),
                Subtotal = totals.Subtotal,
                Discount = totals.Discount,
                ShippingFee = totals.ShippingFee,
                Tax = totals.Tax,
                Total = totals.Total,
                Currency = settings.DefaultCurrency,
                PaymentMethod = request.PaymentMethod,
                Status = StoreOrderStatus.Pending,
                ShippingAddressId = request.ShippingAddressId,
                IdempotencyKey = request.IdempotencyKey,
                CreatedAt = now
            };
            _db.StoreOrders.Add(order);

            foreach (var item in request.Items)
            {
                var product = byId[item.ProductId];

                _db.StoreOrderItems.Add(new StoreOrderItem
                {
                    OrderId = order.Id,
                    ProductId = product.Id,
                    // Snapshots: a later price or name change must not rewrite this order.
                    ProductNameSnapshot = product.Name,
                    ProductSkuSnapshot = product.Sku,
                    ProductImageSnapshot = product.Images.FirstOrDefault(i => i.IsPrimary)?.Url,
                    UnitPrice = product.Price,
                    Quantity = item.Quantity,
                    TotalPrice = Money.Round(product.Price * item.Quantity)
                });

                var before = product.StockQuantity;
                product.StockQuantity -= item.Quantity;
                product.SoldQuantity += item.Quantity;
                product.UpdatedAt = now;

                if (product.StockQuantity <= 0)
                {
                    product.Status = StoreProductStatus.OutOfStock;
                }

                _db.InventoryTransactions.Add(new InventoryTransaction
                {
                    ProductId = product.Id,
                    Type = InventoryTransactionType.Purchase,
                    Quantity = -item.Quantity,
                    BeforeQuantity = before,
                    AfterQuantity = product.StockQuantity,
                    ReferenceType = nameof(StoreOrder),
                    ReferenceId = order.Id,
                    Reason = $"Order {order.OrderNumber}",
                    CreatedAt = now
                });
            }

            var walletType = WalletRules.ToWalletType(request.PaymentMethod);

            var payment = await _wallets.DebitAsync(new WalletMovement
            {
                UserId = userId,
                WalletType = walletType,
                Amount = totals.Total,
                Currency = settings.DefaultCurrency,
                TransactionType = WalletTransactionType.StorePurchase,
                ReferenceType = nameof(StoreOrder),
                ReferenceId = order.Id,
                Description = $"Store order {order.OrderNumber}",
                IdempotencyKey = $"order:{order.Id}"
            }, ct);

            order.WalletTransactionId = payment.TransactionId;
            order.Status = StoreOrderStatus.Paid;
            order.PaidAt = now;

            _db.Payments.Add(new Payment
            {
                UserId = userId,
                StoreOrderId = order.Id,
                PaymentType = PaymentType.StorePurchase,
                Amount = totals.Total,
                Currency = settings.DefaultCurrency,
                Provider = "WALLET",
                Status = PaymentStatus.Completed,
                IdempotencyKey = request.IdempotencyKey,
                CreatedAt = now,
                CompletedAt = now
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            orderId = order.Id;

            _logger.LogInformation(
                "Order {OrderNumber} placed by {UserId} for {Total} {Currency} using {Method}.",
                order.OrderNumber, userId, totals.Total, settings.DefaultCurrency, request.PaymentMethod);
        }

        var dto = await GetOrderAsync(userId, orderId, ct);
        await NotifyOrderCreatedAsync(userId, dto, ct);
        await CheckLowStockAsync(request, ct);

        return dto;
    }

    private async Task NotifyOrderCreatedAsync(Guid userId, StoreOrderDto order, CancellationToken ct)
    {
        try
        {
            await _notifications.NotifyAsync(new NotificationRequest(userId, new NotificationTemplate(
                NotificationType.StoreOrderCreated,
                NotificationKeys.StoreOrderCreatedTitle,
                NotificationKeys.StoreOrderCreatedBody,
                new object[] { order.OrderNumber },
                new Dictionary<string, string>
                {
                    ["type"] = "STORE_ORDER_CREATED",
                    ["orderId"] = order.Id.ToString(),
                    ["orderNumber"] = order.OrderNumber
                })), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order notification failed for {OrderId}.", order.Id);
        }
    }

    private async Task CheckLowStockAsync(CheckoutRequest request, CancellationToken ct)
    {
        try
        {
            var ids = request.Items.Select(i => i.ProductId).Distinct().ToList();

            var low = await _db.StoreProducts
                .AsNoTracking()
                .Where(p => ids.Contains(p.Id) &&
                            p.StockQuantity <= p.LowStockThreshold &&
                            p.StockQuantity > 0)
                .Select(p => new { p.Name, p.StockQuantity })
                .ToListAsync(ct);

            // Surfaced as a structured warning; the admin dashboard's low-stock filter is the
            // primary channel, so this does not fan out a push to every administrator.
            foreach (var item in low)
            {
                _logger.LogWarning("Low stock: {Product} has {Remaining} remaining.", item.Name, item.StockQuantity);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Low stock check failed.");
        }
    }

    private async Task<List<StoreProduct>> LoadProductsAsync(CheckoutRequest request, CancellationToken ct)
    {
        var ids = request.Items.Select(i => i.ProductId).Distinct().ToList();

        return await _db.StoreProducts
            .AsNoTracking()
            .Include(p => p.Images)
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Server-side total. Subtotal, shipping and tax are all derived here; the client's numbers are
    /// never trusted or even read.
    /// </summary>
    private static OrderTotals CalculateTotals(
        CheckoutRequest request, IReadOnlyCollection<StoreProduct> products, StoreSettings settings)
    {
        var byId = products.ToDictionary(p => p.Id);
        decimal subtotal = 0m;
        decimal shipping = 0m;

        foreach (var item in request.Items)
        {
            if (!byId.TryGetValue(item.ProductId, out var product))
            {
                throw new DomainException(ErrorCodes.ProductNotFound, $"Product {item.ProductId} was not found.");
            }

            subtotal += product.Price * item.Quantity;

            if (settings.ShippingEnabled && product.RequiresShipping)
            {
                shipping += product.ShippingFee > 0 ? product.ShippingFee : settings.DefaultShippingFee;
            }
        }

        subtotal = Money.Round(subtotal);
        shipping = Money.Round(shipping);

        var discount = 0m;
        var tax = settings.TaxEnabled
            ? Money.Round((subtotal - discount) * settings.TaxPercentage / 100m)
            : 0m;

        var total = Money.Round(subtotal - discount + shipping + tax);

        return new OrderTotals(subtotal, discount, shipping, tax, total);
    }

    private sealed record OrderTotals(
        decimal Subtotal, decimal Discount, decimal ShippingFee, decimal Tax, decimal Total);

    internal async Task<StoreSettings> GetSettingsAsync(CancellationToken ct)
    {
        var settings = await _db.StoreSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is not null) return settings;

        var created = new StoreSettings();
        _db.StoreSettings.Add(created);
        await _db.SaveChangesAsync(ct);
        return created;
    }

    public async Task<PagedResult<StoreOrderDto>> GetMyOrdersAsync(
        Guid userId, StoreOrderQuery query, CancellationToken ct = default)
    {
        query.UserId = userId;
        return await QueryOrdersAsync(query, ct);
    }

    internal async Task<PagedResult<StoreOrderDto>> QueryOrdersAsync(StoreOrderQuery query, CancellationToken ct)
    {
        var q = _db.StoreOrders.AsNoTracking().AsQueryable();

        if (query.UserId.HasValue) q = q.Where(o => o.UserId == query.UserId.Value);
        if (query.Status.HasValue) q = q.Where(o => o.Status == query.Status.Value);
        if (query.PaymentMethod.HasValue) q = q.Where(o => o.PaymentMethod == query.PaymentMethod.Value);
        if (query.From.HasValue) q = q.Where(o => o.CreatedAt >= query.From.Value);
        if (query.To.HasValue) q = q.Where(o => o.CreatedAt <= query.To.Value);
        if (query.ProductId.HasValue)
            q = q.Where(o => o.Items.Any(i => i.ProductId == query.ProductId.Value));

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(o => o.OrderNumber.ToLower().Contains(term) ||
                             (o.User != null && o.User.FullName.ToLower().Contains(term)) ||
                             (o.User != null && o.User.Email != null && o.User.Email.ToLower().Contains(term)));
        }

        var total = await q.LongCountAsync(ct);

        var orders = await q
            .OrderByDescending(o => o.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Include(o => o.Items)
            .Include(o => o.ShippingAddress)
            .Include(o => o.User)
            .ToListAsync(ct);

        return PagedResult<StoreOrderDto>.Create(
            orders.Select(Map).ToList(), query.Page, query.PageSize, total);
    }

    internal static StoreOrderDto Map(StoreOrder o) => new(
        o.Id, o.OrderNumber, o.UserId, o.User?.FullName,
        o.Subtotal, o.Discount, o.ShippingFee, o.Tax, o.Total, o.Currency,
        o.PaymentMethod, o.Status,
        o.ShippingAddress is null ? null : MapAddress(o.ShippingAddress),
        o.TrackingNumber,
        o.Items.Select(i => new StoreOrderItemDto(
            i.Id, i.ProductId, i.ProductNameSnapshot, i.ProductSkuSnapshot,
            i.ProductImageSnapshot, i.UnitPrice, i.Quantity, i.TotalPrice)).ToList(),
        o.CreatedAt, o.PaidAt, o.ShippedAt, o.DeliveredAt, o.CompletedAt, o.CancelledAt);

    internal static ShippingAddressDto MapAddress(ShippingAddress a) => new(
        a.Id, a.FullName, a.Phone, a.Country, a.City, a.Area,
        a.AddressLine1, a.AddressLine2, a.PostalCode, a.IsDefault);

    public async Task<StoreOrderDto> GetOrderAsync(Guid userId, Guid orderId, CancellationToken ct = default)
    {
        var order = await _db.StoreOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.ShippingAddress)
            .Include(o => o.User)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct)
            ?? throw new DomainException(ErrorCodes.OrderNotFound, "Order not found.");

        return Map(order);
    }

    public async Task<StoreOrderDto> CancelOrderAsync(
        Guid userId, Guid orderId, string? reason, CancellationToken ct = default)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);

        var order = await _db.StoreOrders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct)
            ?? throw new DomainException(ErrorCodes.OrderNotFound, "Order not found.");

        if (!StoreOrderStateMachine.IsCustomerCancellable(order.Status))
        {
            throw new DomainException(
                ErrorCodes.OrderNotCancellable, $"An order in status {order.Status} cannot be cancelled.");
        }

        await CancelAndRefundAsync(order, reason ?? "Cancelled by customer", ct);
        await tx.CommitAsync(ct);

        return await GetOrderAsync(userId, orderId, ct);
    }

    /// <summary>
    /// Restores stock and returns the money to the wallet it came from. A shopping-wallet purchase
    /// is refunded as shopping credit, never converted into withdrawable cash.
    /// </summary>
    internal async Task CancelAndRefundAsync(StoreOrder order, string reason, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var wasPaid = order.Status is StoreOrderStatus.Paid or StoreOrderStatus.Processing
            or StoreOrderStatus.Delivered or StoreOrderStatus.Completed;

        if (StoreOrderStateMachine.ReleasesStock(order.Status))
        {
            var productIds = order.Items.Select(i => i.ProductId).Distinct().OrderBy(id => id).ToArray();

            var products = await _db.StoreProducts
                .FromSqlRaw(
                    """SELECT *, xmin FROM "StoreProducts" WHERE "Id" = ANY({0}) ORDER BY "Id" FOR UPDATE""",
                    productIds)
                .ToListAsync(ct);

            foreach (var item in order.Items)
            {
                var product = products.FirstOrDefault(p => p.Id == item.ProductId);
                if (product is null) continue;

                var before = product.StockQuantity;
                product.StockQuantity += item.Quantity;
                product.SoldQuantity = Math.Max(0, product.SoldQuantity - item.Quantity);
                product.UpdatedAt = now;

                if (product.Status == StoreProductStatus.OutOfStock && product.StockQuantity > 0)
                {
                    product.Status = StoreProductStatus.Active;
                }

                _db.InventoryTransactions.Add(new InventoryTransaction
                {
                    ProductId = product.Id,
                    Type = InventoryTransactionType.Cancellation,
                    Quantity = item.Quantity,
                    BeforeQuantity = before,
                    AfterQuantity = product.StockQuantity,
                    ReferenceType = nameof(StoreOrder),
                    ReferenceId = order.Id,
                    Reason = reason,
                    CreatedAt = now
                });
            }
        }

        if (wasPaid && order.RefundWalletTransactionId is null && order.Total > 0)
        {
            var refund = await _wallets.CreditAsync(new WalletMovement
            {
                UserId = order.UserId,
                WalletType = WalletRules.ToWalletType(order.PaymentMethod),
                Amount = order.Total,
                Currency = order.Currency,
                TransactionType = WalletTransactionType.Refund,
                ReferenceType = nameof(StoreOrder),
                ReferenceId = order.Id,
                CreditSource = order.PaymentMethod == StorePaymentMethod.ShoppingWallet
                    ? ShoppingCreditSource.StoreRefund
                    : null,
                Description = $"Refund for order {order.OrderNumber}",
                IdempotencyKey = $"order-refund:{order.Id}"
            }, ct);

            order.RefundWalletTransactionId = refund.TransactionId;
            order.RefundedAt = now;
        }

        order.Status = StoreOrderStatus.Cancelled;
        order.CancelledAt = now;
        order.CancellationReason = reason;

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ShippingAddressDto>> GetAddressesAsync(Guid userId, CancellationToken ct = default)
    {
        var addresses = await _db.ShippingAddresses
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.IsDefault).ThenByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        return addresses.Select(MapAddress).ToList();
    }

    public async Task<ShippingAddressDto> SaveAddressAsync(
        Guid userId, SaveShippingAddressRequest request, CancellationToken ct = default)
    {
        if (request.IsDefault)
        {
            var others = await _db.ShippingAddresses.Where(a => a.UserId == userId && a.IsDefault).ToListAsync(ct);
            foreach (var other in others) other.IsDefault = false;
        }

        var address = new ShippingAddress
        {
            UserId = userId,
            FullName = request.FullName,
            Phone = request.Phone,
            Country = request.Country,
            City = request.City,
            Area = request.Area,
            AddressLine1 = request.AddressLine1,
            AddressLine2 = request.AddressLine2,
            PostalCode = request.PostalCode,
            IsDefault = request.IsDefault,
            CreatedAt = _clock.UtcNow
        };

        _db.ShippingAddresses.Add(address);
        await _db.SaveChangesAsync(ct);

        return MapAddress(address);
    }

    public async Task DeleteAddressAsync(Guid userId, Guid addressId, CancellationToken ct = default)
    {
        var address = await _db.ShippingAddresses
            .FirstOrDefaultAsync(a => a.Id == addressId && a.UserId == userId, ct)
            ?? throw new DomainException(ErrorCodes.NotFound, "Address not found.");

        _db.ShippingAddresses.Remove(address);
        await _db.SaveChangesAsync(ct);
    }
}
