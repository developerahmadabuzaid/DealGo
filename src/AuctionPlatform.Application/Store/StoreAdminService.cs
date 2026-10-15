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
/// Store administration. Every mutating method writes an audit record, and stock only ever moves
/// alongside an InventoryTransaction.
/// </summary>
public sealed class StoreAdminService : IStoreAdminService
{
    private readonly IApplicationDbContext _db;
    private readonly StoreOrderService _orders;
    private readonly IWalletService _wallets;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _currentUser;
    private readonly ICacheService _cache;
    private readonly INotificationService _notifications;
    private readonly ILogger<StoreAdminService> _logger;

    public StoreAdminService(
        IApplicationDbContext db,
        StoreOrderService orders,
        IWalletService wallets,
        IDateTimeProvider clock,
        IAuditService audit,
        ICurrentUserService currentUser,
        ICacheService cache,
        INotificationService notifications,
        ILogger<StoreAdminService> logger)
    {
        _db = db;
        _orders = orders;
        _wallets = wallets;
        _clock = clock;
        _audit = audit;
        _currentUser = currentUser;
        _cache = cache;
        _notifications = notifications;
        _logger = logger;
    }

    // ---------------------------------------------------------------- overview

    public async Task<StoreOverviewDto> GetOverviewAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsEntityAsync(ct);
        var currency = settings.DefaultCurrency;

        var products = await _db.StoreProducts.AsNoTracking()
            .GroupBy(p => 1)
            .Select(g => new
            {
                Total = g.Count(p => p.Status != StoreProductStatus.Archived),
                Active = g.Count(p => p.Status == StoreProductStatus.Active),
                OutOfStock = g.Count(p => p.Status == StoreProductStatus.OutOfStock),
                LowStock = g.Count(p => p.StockQuantity > 0 && p.StockQuantity <= p.LowStockThreshold)
            })
            .FirstOrDefaultAsync(ct);

        var totalOrders = await _db.StoreOrders.AsNoTracking().LongCountAsync(ct);

        var pendingOrders = await _db.StoreOrders.AsNoTracking()
            .LongCountAsync(o => o.Status == StoreOrderStatus.Pending ||
                                 o.Status == StoreOrderStatus.Paid ||
                                 o.Status == StoreOrderStatus.Processing, ct);

        var completedOrders = await _db.StoreOrders.AsNoTracking()
            .LongCountAsync(o => o.Status == StoreOrderStatus.Completed ||
                                 o.Status == StoreOrderStatus.Delivered, ct);

        var revenue = await _db.StoreOrders.AsNoTracking()
            .Where(o => o.Status != StoreOrderStatus.Cancelled && o.Status != StoreOrderStatus.Refunded)
            .SumAsync(o => (decimal?)o.Total, ct) ?? 0m;

        var credit = await _wallets.GetShoppingCreditSummaryAsync(currency, ct);

        return new StoreOverviewDto(
            products?.Total ?? 0, products?.Active ?? 0, products?.OutOfStock ?? 0, products?.LowStock ?? 0,
            totalOrders, pendingOrders, completedOrders, Money.Round(revenue),
            credit.TotalUsed, credit.TotalIssued, credit.Outstanding, currency);
    }

    // ---------------------------------------------------------------- products

    public async Task<PagedResult<StoreProductListItemDto>> GetProductsAsync(
        StoreProductQuery query, CancellationToken ct = default)
    {
        var q = _db.StoreProducts.AsNoTracking().AsQueryable();

        if (query.Status.HasValue) q = q.Where(p => p.Status == query.Status.Value);
        if (query.CategoryId.HasValue)
        {
            var categoryId = query.CategoryId.Value;
            q = q.Where(p => p.CategoryId == categoryId ||
                             (p.Category != null && p.Category.ParentId == categoryId));
        }
        if (query.MinPrice.HasValue) q = q.Where(p => p.Price >= query.MinPrice.Value);
        if (query.MaxPrice.HasValue) q = q.Where(p => p.Price <= query.MaxPrice.Value);
        if (query.FeaturedOnly == true) q = q.Where(p => p.IsFeatured);
        if (query.InStockOnly == true) q = q.Where(p => p.StockQuantity - p.ReservedQuantity > 0);
        if (query.LowStockOnly == true)
            q = q.Where(p => p.StockQuantity > 0 && p.StockQuantity <= p.LowStockThreshold);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(p => p.Name.ToLower().Contains(term) ||
                             p.Sku.ToLower().Contains(term) ||
                             (p.Category != null && p.Category.Name.ToLower().Contains(term)));
        }

        var total = await q.LongCountAsync(ct);

        var items = await q
            .OrderByDescending(p => p.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(StoreCatalogService.Projection)
            .ToListAsync(ct);

        return PagedResult<StoreProductListItemDto>.Create(items, query.Page, query.PageSize, total);
    }

    public async Task<StoreProductDetailDto> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await LoadProductAsync(productId, ct);
        return StoreCatalogService.MapDetail(product);
    }

    public async Task<StoreProductDetailDto> CreateProductAsync(
        CreateStoreProductRequest request, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var settings = await GetSettingsEntityAsync(ct);

        if (await _db.StoreProducts.AnyAsync(p => p.Sku == request.Sku, ct))
        {
            throw new DomainException(ErrorCodes.DuplicateSku, $"SKU {request.Sku} is already in use.");
        }

        if (request.CategoryId.HasValue &&
            !await _db.StoreCategories.AnyAsync(c => c.Id == request.CategoryId.Value, ct))
        {
            throw new DomainException(ErrorCodes.CategoryNotFound, "Category not found.");
        }

        var product = new StoreProduct
        {
            Name = request.Name,
            Slug = Slugify(request.Slug ?? request.Name),
            Description = request.Description,
            CategoryId = request.CategoryId,
            Sku = request.Sku,
            Price = Money.Round(request.Price),
            CompareAtPrice = OfferPrice(request.CompareAtPrice, request.Price),
            Currency = request.Currency ?? settings.DefaultCurrency,
            StockQuantity = request.StockQuantity,
            LowStockThreshold = request.LowStockThreshold ?? settings.LowStockThreshold,
            Status = request.Status ?? StoreProductStatus.Draft,
            PaymentMode = request.PaymentMode ?? StorePaymentMode.ShoppingWallet,
            IsFeatured = request.IsFeatured ?? false,
            RequiresShipping = request.RequiresShipping ?? true,
            ShippingFee = request.ShippingFee ?? 0m,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.StoreProducts.Add(product);
        ApplyImages(product, request.Images);

        if (request.StockQuantity > 0)
        {
            _db.InventoryTransactions.Add(new InventoryTransaction
            {
                ProductId = product.Id,
                Type = InventoryTransactionType.Initial,
                Quantity = request.StockQuantity,
                BeforeQuantity = 0,
                AfterQuantity = request.StockQuantity,
                Reason = "Initial stock",
                AdminUserId = _currentUser.UserId,
                CreatedAt = now
            });
        }

        await _db.SaveChangesAsync(ct);
        await InvalidateCatalogCacheAsync(ct);

        await _audit.LogAsync(AuditActionType.StoreProductCreated, nameof(StoreProduct), product.Id,
            null, new { product.Name, product.Sku, product.Price, product.StockQuantity }, ct: ct);

        return StoreCatalogService.MapDetail(await LoadProductAsync(product.Id, ct));
    }

    public async Task<StoreProductDetailDto> UpdateProductAsync(
        Guid productId, UpdateStoreProductRequest request, CancellationToken ct = default)
    {
        var product = await LoadProductAsync(productId, ct);

        var before = new { product.Name, product.Sku, product.Price, product.PaymentMode, product.Status };

        if (request.Sku is not null && request.Sku != product.Sku)
        {
            if (await _db.StoreProducts.AnyAsync(p => p.Sku == request.Sku && p.Id != productId, ct))
            {
                throw new DomainException(ErrorCodes.DuplicateSku, $"SKU {request.Sku} is already in use.");
            }
            product.Sku = request.Sku;
        }

        if (request.Name is not null) product.Name = request.Name;
        if (request.Slug is not null) product.Slug = Slugify(request.Slug);
        if (request.Description is not null) product.Description = request.Description;
        if (request.CategoryId.HasValue) product.CategoryId = request.CategoryId;

        // Changing the price never rewrites existing orders: those hold their own snapshots.
        if (request.Price.HasValue) product.Price = Money.Round(request.Price.Value);

        // Sent together or apart, the pair is always checked against the price that will apply.
        if (request.CompareAtPrice.HasValue || request.Price.HasValue)
        {
            product.CompareAtPrice =
                OfferPrice(request.CompareAtPrice ?? product.CompareAtPrice, product.Price);
        }

        if (request.PaymentMode.HasValue) product.PaymentMode = request.PaymentMode.Value;
        if (request.IsFeatured.HasValue) product.IsFeatured = request.IsFeatured.Value;
        if (request.RequiresShipping.HasValue) product.RequiresShipping = request.RequiresShipping.Value;
        if (request.ShippingFee.HasValue) product.ShippingFee = request.ShippingFee.Value;
        if (request.LowStockThreshold.HasValue) product.LowStockThreshold = request.LowStockThreshold.Value;

        if (request.Images is not null)
        {
            _db.StoreProductImages.RemoveRange(product.Images);
            ApplyImages(product, request.Images);
        }

        product.UpdatedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(ct);
        await InvalidateCatalogCacheAsync(ct);

        await _audit.LogAsync(AuditActionType.StoreProductUpdated, nameof(StoreProduct), productId,
            before, new { product.Name, product.Sku, product.Price, product.PaymentMode, product.Status }, ct: ct);

        return StoreCatalogService.MapDetail(await LoadProductAsync(productId, ct));
    }

    public async Task<StoreProductDetailDto> SetProductStatusAsync(
        Guid productId, StoreProductStatus status, CancellationToken ct = default)
    {
        var product = await LoadProductAsync(productId, ct);
        var before = product.Status;

        if (status == StoreProductStatus.Active && product.AvailableQuantity <= 0)
        {
            status = StoreProductStatus.OutOfStock;
        }

        product.Status = status;
        product.UpdatedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(ct);
        await InvalidateCatalogCacheAsync(ct);

        await _audit.LogAsync(AuditActionType.StoreProductUpdated, nameof(StoreProduct), productId,
            new { Status = before }, new { Status = status }, ct: ct);

        return StoreCatalogService.MapDetail(product);
    }

    /// <summary>
    /// Products referenced by orders are archived rather than removed, so historical orders keep
    /// resolving. Only a product that has never sold is physically deleted.
    /// </summary>
    public async Task DeleteProductAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await LoadProductAsync(productId, ct);

        var referenced = await _db.StoreOrderItems.AnyAsync(i => i.ProductId == productId, ct);

        if (referenced)
        {
            product.Status = StoreProductStatus.Archived;
            product.UpdatedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(AuditActionType.StoreProductArchived, nameof(StoreProduct), productId,
                null, new { product.Name }, "Archived because it is referenced by orders", ct);
        }
        else
        {
            _db.StoreProductImages.RemoveRange(product.Images);
            _db.StoreProducts.Remove(product);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(AuditActionType.StoreProductArchived, nameof(StoreProduct), productId,
                new { product.Name }, null, "Deleted", ct);
        }

        await InvalidateCatalogCacheAsync(ct);
    }

    private void ApplyImages(StoreProduct product, IReadOnlyList<StoreProductImageRequest>? images)
    {
        if (images is null || images.Count == 0) return;

        var hasPrimary = images.Any(i => i.IsPrimary);

        for (var i = 0; i < images.Count; i++)
        {
            _db.StoreProductImages.Add(new StoreProductImage
            {
                ProductId = product.Id,
                Url = images[i].Url,
                ThumbnailUrl = images[i].ThumbnailUrl,
                IsPrimary = hasPrimary ? images[i].IsPrimary : i == 0,
                DisplayOrder = images[i].DisplayOrder,
                CreatedAt = _clock.UtcNow
            });
        }
    }

    private async Task<StoreProduct> LoadProductAsync(Guid productId, CancellationToken ct) =>
        await _db.StoreProducts
            .Include(p => p.Images)
            .Include(p => p.Category).ThenInclude(c => c!.Parent)
            .FirstOrDefaultAsync(p => p.Id == productId, ct)
        ?? throw new DomainException(ErrorCodes.ProductNotFound, "Product not found.");

    // ---------------------------------------------------------------- categories

    public async Task<IReadOnlyList<StoreCategoryDto>> GetCategoriesAsync(CancellationToken ct = default) =>
        await _db.StoreCategories
            .AsNoTracking()
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .Select(c => new StoreCategoryDto(
                c.Id, c.Name, c.Slug, c.Description, c.ImageUrl, c.DisplayOrder, c.Status,
                c.Products.Count, c.ParentId, c.Parent != null ? c.Parent.Name : null))
            .ToListAsync(ct);

    /*
     * Departments go two levels deep and no further: a main department, and sub-departments
     * under it. So the parent must itself be a main department, and a department that already
     * has sub-departments cannot be moved under another one.
     */
    private async Task<StoreCategory> CheckParentAsync(Guid? categoryId, Guid parentId, CancellationToken ct)
    {
        if (categoryId == parentId)
        {
            throw new DomainException(ErrorCodes.CategoryTooDeep, "A department cannot sit under itself.");
        }

        var parent = await _db.StoreCategories.FirstOrDefaultAsync(c => c.Id == parentId, ct)
            ?? throw new DomainException(ErrorCodes.CategoryNotFound, "Main department not found.");

        if (parent.ParentId is not null)
        {
            throw new DomainException(ErrorCodes.CategoryTooDeep,
                "Only two levels are allowed: choose a main department as the parent.");
        }

        if (categoryId.HasValue && await _db.StoreCategories.AnyAsync(c => c.ParentId == categoryId, ct))
        {
            throw new DomainException(ErrorCodes.CategoryTooDeep,
                "This department has sub-departments of its own, so it cannot become a sub-department.");
        }

        return parent;
    }

    public async Task<StoreCategoryDto> CreateCategoryAsync(
        CreateStoreCategoryRequest request, CancellationToken ct = default)
    {
        var parent = request.ParentId is { } pid && pid != Guid.Empty
            ? await CheckParentAsync(null, pid, ct)
            : null;

        var category = new StoreCategory
        {
            ParentId = parent?.Id,
            Name = request.Name,
            Slug = Slugify(request.Slug ?? request.Name),
            Description = request.Description,
            ImageUrl = request.ImageUrl,
            DisplayOrder = request.DisplayOrder ?? 0,
            Status = StoreCategoryStatus.Active,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };

        _db.StoreCategories.Add(category);
        await _db.SaveChangesAsync(ct);
        await InvalidateCatalogCacheAsync(ct);

        await _audit.LogAsync(AuditActionType.StoreCategoryCreated, nameof(StoreCategory), category.Id,
            null, new { category.Name }, ct: ct);

        return new StoreCategoryDto(category.Id, category.Name, category.Slug, category.Description,
            category.ImageUrl, category.DisplayOrder, category.Status, 0, parent?.Id, parent?.Name);
    }

    public async Task<StoreCategoryDto> UpdateCategoryAsync(
        Guid categoryId, UpdateStoreCategoryRequest request, CancellationToken ct = default)
    {
        var category = await _db.StoreCategories.FirstOrDefaultAsync(c => c.Id == categoryId, ct)
            ?? throw new DomainException(ErrorCodes.CategoryNotFound, "Category not found.");

        var before = new { category.Name, category.Status };

        if (request.Name is not null) category.Name = request.Name;
        if (request.Slug is not null) category.Slug = Slugify(request.Slug);
        if (request.Description is not null) category.Description = request.Description;
        if (request.ImageUrl is not null) category.ImageUrl = request.ImageUrl;
        if (request.DisplayOrder.HasValue) category.DisplayOrder = request.DisplayOrder.Value;
        if (request.Status.HasValue) category.Status = request.Status.Value;

        if (request.ParentId == Guid.Empty)
        {
            category.ParentId = null;
        }
        else if (request.ParentId is { } parentId && parentId != category.ParentId)
        {
            category.ParentId = (await CheckParentAsync(categoryId, parentId, ct)).Id;
        }

        category.UpdatedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(ct);
        await InvalidateCatalogCacheAsync(ct);

        await _audit.LogAsync(AuditActionType.StoreCategoryUpdated, nameof(StoreCategory), categoryId,
            before, new { category.Name, category.Status }, ct: ct);

        var count = await _db.StoreProducts.CountAsync(p => p.CategoryId == categoryId, ct);
        var parentName = category.ParentId is { } pid2
            ? await _db.StoreCategories.Where(c => c.Id == pid2).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;

        return new StoreCategoryDto(category.Id, category.Name, category.Slug, category.Description,
            category.ImageUrl, category.DisplayOrder, category.Status, count, category.ParentId, parentName);
    }

    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default)
    {
        var category = await _db.StoreCategories.FirstOrDefaultAsync(c => c.Id == categoryId, ct)
            ?? throw new DomainException(ErrorCodes.CategoryNotFound, "Category not found.");

        // Sub-departments are moved or deleted first, so none is left without its main department.
        if (await _db.StoreCategories.AnyAsync(c => c.ParentId == categoryId, ct))
        {
            throw new DomainException(ErrorCodes.CategoryHasSubcategories,
                "Move or delete this department's sub-departments first.");
        }

        var hasProducts = await _db.StoreProducts.AnyAsync(p => p.CategoryId == categoryId, ct);

        if (hasProducts)
        {
            category.Status = StoreCategoryStatus.Archived;
            category.UpdatedAt = _clock.UtcNow;
        }
        else
        {
            _db.StoreCategories.Remove(category);
        }

        await _db.SaveChangesAsync(ct);
        await InvalidateCatalogCacheAsync(ct);

        await _audit.LogAsync(AuditActionType.StoreCategoryUpdated, nameof(StoreCategory), categoryId,
            new { category.Name }, null, hasProducts ? "Archived" : "Deleted", ct);
    }

    // ---------------------------------------------------------------- inventory

    public async Task<PagedResult<InventoryRowDto>> GetInventoryAsync(
        StoreProductQuery query, CancellationToken ct = default)
    {
        var q = _db.StoreProducts.AsNoTracking().Where(p => p.Status != StoreProductStatus.Archived);

        if (query.CategoryId.HasValue)
        {
            var categoryId = query.CategoryId.Value;
            q = q.Where(p => p.CategoryId == categoryId ||
                             (p.Category != null && p.Category.ParentId == categoryId));
        }
        if (query.LowStockOnly == true)
            q = q.Where(p => p.StockQuantity <= p.LowStockThreshold);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(p => p.Name.ToLower().Contains(term) || p.Sku.ToLower().Contains(term));
        }

        var total = await q.LongCountAsync(ct);

        var items = await q
            .OrderBy(p => p.StockQuantity)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(p => new InventoryRowDto(
                p.Id, p.Name, p.Sku, p.StockQuantity, p.ReservedQuantity,
                p.StockQuantity - p.ReservedQuantity, p.SoldQuantity, p.LowStockThreshold,
                p.StockQuantity <= p.LowStockThreshold, p.Status))
            .ToListAsync(ct);

        return PagedResult<InventoryRowDto>.Create(items, query.Page, query.PageSize, total);
    }

    /// <summary>Stock adjustments are always audited and always paired with a transaction row.</summary>
    public async Task<InventoryTransactionDto> AdjustInventoryAsync(
        Guid productId, AdjustInventoryRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A reason is required for stock adjustments.");
        }

        await using var tx = await _db.BeginTransactionAsync(ct);

        var product = await _db.StoreProducts
            .FromSqlRaw("""SELECT *, xmin FROM "StoreProducts" WHERE "Id" = {0} FOR UPDATE""", productId)
            .FirstOrDefaultAsync(ct)
            ?? throw new DomainException(ErrorCodes.ProductNotFound, "Product not found.");

        var before = product.StockQuantity;
        var after = before + request.Delta;

        if (after < 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Stock cannot go negative.");
        }

        product.StockQuantity = after;
        product.UpdatedAt = _clock.UtcNow;

        if (product.Status == StoreProductStatus.OutOfStock && after > 0)
        {
            product.Status = StoreProductStatus.Active;
        }
        else if (product.Status == StoreProductStatus.Active && after == 0)
        {
            product.Status = StoreProductStatus.OutOfStock;
        }

        var transaction = new InventoryTransaction
        {
            ProductId = productId,
            Type = request.Type ?? (request.Delta > 0
                ? InventoryTransactionType.Restock
                : InventoryTransactionType.Adjustment),
            Quantity = request.Delta,
            BeforeQuantity = before,
            AfterQuantity = after,
            Reason = request.Reason,
            AdminUserId = _currentUser.UserId,
            CreatedAt = _clock.UtcNow
        };

        _db.InventoryTransactions.Add(transaction);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await _audit.LogAsync(AuditActionType.InventoryAdjusted, nameof(StoreProduct), productId,
            new { Stock = before }, new { Stock = after }, request.Reason, ct);

        if (after > 0 && after <= product.LowStockThreshold)
        {
            _logger.LogWarning("Low stock after adjustment: {Product} has {Remaining}.", product.Name, after);
        }

        return new InventoryTransactionDto(
            transaction.Id, productId, product.Name, transaction.Type, transaction.Quantity,
            before, after, transaction.Reason, transaction.AdminUserId, transaction.CreatedAt);
    }

    public async Task<PagedResult<InventoryTransactionDto>> GetInventoryHistoryAsync(
        Guid? productId, PagedQuery query, CancellationToken ct = default)
    {
        var q = _db.InventoryTransactions.AsNoTracking().AsQueryable();

        if (productId.HasValue) q = q.Where(t => t.ProductId == productId.Value);

        var total = await q.LongCountAsync(ct);

        var items = await q
            .OrderByDescending(t => t.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(t => new InventoryTransactionDto(
                t.Id, t.ProductId, t.Product != null ? t.Product.Name : string.Empty,
                t.Type, t.Quantity, t.BeforeQuantity, t.AfterQuantity,
                t.Reason, t.AdminUserId, t.CreatedAt))
            .ToListAsync(ct);

        return PagedResult<InventoryTransactionDto>.Create(items, query.Page, query.PageSize, total);
    }

    // ---------------------------------------------------------------- orders

    public Task<PagedResult<StoreOrderDto>> GetOrdersAsync(StoreOrderQuery query, CancellationToken ct = default) =>
        _orders.QueryOrdersAsync(query, ct);

    public async Task<StoreOrderDto> GetOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await _db.StoreOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.ShippingAddress)
            .Include(o => o.User)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new DomainException(ErrorCodes.OrderNotFound, "Order not found.");

        return StoreOrderService.Map(order);
    }

    public async Task<StoreOrderDto> TransitionOrderAsync(
        Guid orderId, StoreOrderStatus status, string? note, CancellationToken ct = default)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);

        var order = await _db.StoreOrders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new DomainException(ErrorCodes.OrderNotFound, "Order not found.");

        var before = order.Status;

        if (status == StoreOrderStatus.Cancelled)
        {
            await _orders.CancelAndRefundAsync(order, note ?? "Cancelled by administrator", ct);
            await tx.CommitAsync(ct);

            await _audit.LogAsync(AuditActionType.StoreOrderCancelled, nameof(StoreOrder), orderId,
                new { Status = before }, new { Status = status }, note, ct);

            return await GetOrderAsync(orderId, ct);
        }

        StoreOrderStateMachine.EnsureCanTransition(before, status);

        var now = _clock.UtcNow;
        order.Status = status;

        switch (status)
        {
            case StoreOrderStatus.Processing: order.ProcessingAt = now; break;
            case StoreOrderStatus.Shipped:
                order.ShippedAt = now;
                if (!string.IsNullOrWhiteSpace(note)) order.TrackingNumber = note;
                break;
            case StoreOrderStatus.Delivered: order.DeliveredAt = now; break;
            case StoreOrderStatus.Completed: order.CompletedAt = now; break;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await _audit.LogAsync(AuditActionType.StoreOrderStatusChanged, nameof(StoreOrder), orderId,
            new { Status = before }, new { Status = status }, note, ct);

        await NotifyOrderStatusAsync(order, status, ct);

        return await GetOrderAsync(orderId, ct);
    }

    private async Task NotifyOrderStatusAsync(StoreOrder order, StoreOrderStatus status, CancellationToken ct)
    {
        NotificationTemplate? template = status switch
        {
            StoreOrderStatus.Shipped => new NotificationTemplate(
                NotificationType.OrderShipped, NotificationKeys.OrderShippedTitle,
                NotificationKeys.OrderShippedBody, new object[] { order.OrderNumber },
                new Dictionary<string, string> { ["type"] = "ORDER_SHIPPED", ["orderId"] = order.Id.ToString() }),

            StoreOrderStatus.Delivered => new NotificationTemplate(
                NotificationType.OrderDelivered, NotificationKeys.OrderDeliveredTitle,
                NotificationKeys.OrderDeliveredBody, new object[] { order.OrderNumber },
                new Dictionary<string, string> { ["type"] = "ORDER_DELIVERED", ["orderId"] = order.Id.ToString() }),

            _ => null
        };

        if (template is null) return;

        try
        {
            await _notifications.NotifyAsync(new NotificationRequest(order.UserId, template), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order status notification failed for {OrderId}.", order.Id);
        }
    }

    /// <summary>
    /// Refunds an order back to the wallet it was paid from. A shopping-wallet purchase returns as
    /// shopping credit; it never becomes withdrawable cash.
    /// </summary>
    public async Task<StoreOrderDto> RefundOrderAsync(Guid orderId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A reason is required for refunds.");
        }

        await using var tx = await _db.BeginTransactionAsync(ct);

        var order = await _db.StoreOrders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new DomainException(ErrorCodes.OrderNotFound, "Order not found.");

        if (order.Status == StoreOrderStatus.Refunded)
        {
            await tx.CommitAsync(ct);
            return await GetOrderAsync(orderId, ct);
        }

        StoreOrderStateMachine.EnsureCanTransition(order.Status, StoreOrderStatus.Refunded);

        var before = order.Status;
        var now = _clock.UtcNow;

        if (order.RefundWalletTransactionId is null && order.Total > 0)
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
                Description = $"Refund for order {order.OrderNumber}: {reason}",
                IdempotencyKey = $"order-refund:{order.Id}"
            }, ct);

            order.RefundWalletTransactionId = refund.TransactionId;
        }

        order.Status = StoreOrderStatus.Refunded;
        order.RefundedAt = now;
        order.CancellationReason = reason;

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await _audit.LogAsync(AuditActionType.StoreOrderRefunded, nameof(StoreOrder), orderId,
            new { Status = before }, new { Status = StoreOrderStatus.Refunded, order.Total }, reason, ct);

        return await GetOrderAsync(orderId, ct);
    }

    // ---------------------------------------------------------------- settings

    public async Task<StoreSettingsDto> GetSettingsAsync(CancellationToken ct = default) =>
        MapSettings(await GetSettingsEntityAsync(ct));

    public async Task<StoreSettingsDto> UpdateSettingsAsync(StoreSettingsDto request, CancellationToken ct = default)
    {
        var settings = await GetSettingsEntityAsync(ct);
        var before = MapSettings(settings);

        settings.StoreEnabled = request.StoreEnabled;
        settings.ShoppingWalletEnabled = request.ShoppingWalletEnabled;
        settings.AllowShoppingWalletPurchases = request.AllowShoppingWalletPurchases;
        settings.AllowMainWalletPurchases = request.AllowMainWalletPurchases;
        settings.DefaultCurrency = request.DefaultCurrency;
        settings.TaxEnabled = request.TaxEnabled;
        settings.TaxPercentage = request.TaxPercentage;
        settings.ShippingEnabled = request.ShippingEnabled;
        settings.DefaultShippingFee = request.DefaultShippingFee;
        settings.MinimumOrderAmount = request.MinimumOrderAmount;
        settings.MaximumOrderAmount = request.MaximumOrderAmount;
        settings.ShoppingCreditExpirationEnabled = request.ShoppingCreditExpirationEnabled;
        settings.ShoppingCreditExpirationDays = request.ShoppingCreditExpirationDays;
        settings.RefundPolicy = request.RefundPolicy;
        settings.LowStockThreshold = request.LowStockThreshold;
        settings.UpdatedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.StoreSettingsChanged, nameof(StoreSettings), settings.Id,
            before, MapSettings(settings), ct: ct);

        return MapSettings(settings);
    }

    private async Task<StoreSettings> GetSettingsEntityAsync(CancellationToken ct)
    {
        var settings = await _db.StoreSettings.FirstOrDefaultAsync(ct);
        if (settings is not null) return settings;

        settings = new StoreSettings();
        _db.StoreSettings.Add(settings);
        await _db.SaveChangesAsync(ct);
        return settings;
    }

    private static StoreSettingsDto MapSettings(StoreSettings s) => new(
        s.StoreEnabled, s.ShoppingWalletEnabled, s.AllowShoppingWalletPurchases,
        s.AllowMainWalletPurchases, s.DefaultCurrency, s.TaxEnabled, s.TaxPercentage,
        s.ShippingEnabled, s.DefaultShippingFee, s.MinimumOrderAmount, s.MaximumOrderAmount,
        s.ShoppingCreditExpirationEnabled, s.ShoppingCreditExpirationDays,
        s.RefundPolicy, s.LowStockThreshold);

    /// <summary>
    /// The price before an offer. Zero or less clears the offer; anything not above the price
    /// itself is refused, because that would show a discount the buyer is not getting.
    /// </summary>
    private static decimal? OfferPrice(decimal? compareAt, decimal price)
    {
        if (compareAt is not { } was || was <= 0m) return null;

        was = Money.Round(was);

        if (was <= Money.Round(price))
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed,
                "The price before the offer must be higher than the price.");
        }

        return was;
    }

    private Task InvalidateCatalogCacheAsync(CancellationToken ct) =>
        _cache.RemoveByPrefixAsync("store:", ct);

    internal static string Slugify(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}
