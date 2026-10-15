using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Store;

public sealed class StoreCatalogService : IStoreCatalogService
{
    private readonly IApplicationDbContext _db;
    private readonly IWalletService _wallets;
    private readonly ICacheService _cache;

    public StoreCatalogService(IApplicationDbContext db, IWalletService wallets, ICacheService cache)
    {
        _db = db;
        _wallets = wallets;
        _cache = cache;
    }

    public async Task<StoreHomeDto> GetHomeAsync(Guid? userId, CancellationToken ct = default)
    {
        var categories = await GetCategoriesAsync(ct);

        var featured = await PublicProducts()
            .Where(p => p.IsFeatured)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(10)
            .Select(Projection)
            .ToListAsync(ct);

        var newArrivals = await PublicProducts()
            .OrderByDescending(p => p.CreatedAt)
            .Take(10)
            .Select(Projection)
            .ToListAsync(ct);

        var popular = await PublicProducts()
            .OrderByDescending(p => p.SoldQuantity)
            .ThenByDescending(p => p.ViewCount)
            .Take(10)
            .Select(Projection)
            .ToListAsync(ct);

        var creditEligible = await PublicProducts()
            .Where(p => p.PaymentMode == StorePaymentMode.ShoppingWallet ||
                        p.PaymentMode == StorePaymentMode.Both)
            .OrderBy(p => p.Price)
            .Take(10)
            .Select(Projection)
            .ToListAsync(ct);

        // Only a real discount counts: the price must be below the price before the offer.
        var offers = await PublicProducts()
            .Where(p => p.CompareAtPrice != null && p.CompareAtPrice > p.Price)
            .OrderByDescending(p => (p.CompareAtPrice!.Value - p.Price) / p.CompareAtPrice.Value)
            .Take(12)
            .Select(Projection)
            .ToListAsync(ct);

        var creditBalance = 0m;
        var currency = Money.DefaultCurrency;

        if (userId.HasValue)
        {
            var wallets = await _wallets.GetWalletsAsync(userId.Value, ct);
            creditBalance = wallets.Shopping.Balance;
            currency = wallets.Currency;
        }

        return new StoreHomeDto(
            categories, featured, newArrivals, popular, creditEligible, offers, creditBalance, currency);
    }

    private IQueryable<StoreProduct> PublicProducts() =>
        _db.StoreProducts
            .AsNoTracking()
            .Where(p => p.Status == StoreProductStatus.Active || p.Status == StoreProductStatus.OutOfStock);

    /// <summary>Shared list projection so every catalog surface reports availability identically.</summary>
    internal static System.Linq.Expressions.Expression<Func<StoreProduct, StoreProductListItemDto>> Projection =>
        p => new StoreProductListItemDto(
            p.Id, p.Name, p.Slug, p.Sku, p.CategoryId,
            p.Category != null ? p.Category.Name : null,
            p.Price, p.CompareAtPrice, p.Currency,
            p.Images.Where(i => i.IsPrimary).Select(i => i.Url).FirstOrDefault(),
            p.StockQuantity - p.ReservedQuantity,
            p.StockQuantity - p.ReservedQuantity > 0,
            p.Status, p.PaymentMode,
            p.PaymentMode == StorePaymentMode.ShoppingWallet || p.PaymentMode == StorePaymentMode.Both,
            p.IsFeatured);

    public async Task<PagedResult<StoreProductListItemDto>> GetProductsAsync(
        StoreProductQuery query, CancellationToken ct = default)
    {
        var q = PublicProducts();

        // A main department shows its own products and every sub-department's.
        if (query.CategoryId.HasValue)
        {
            var id = query.CategoryId.Value;
            q = q.Where(p => p.CategoryId == id || (p.Category != null && p.Category.ParentId == id));
        }
        if (query.MinPrice.HasValue) q = q.Where(p => p.Price >= query.MinPrice.Value);
        if (query.MaxPrice.HasValue) q = q.Where(p => p.Price <= query.MaxPrice.Value);
        if (query.FeaturedOnly == true) q = q.Where(p => p.IsFeatured);
        if (query.OnOfferOnly == true) q = q.Where(p => p.CompareAtPrice != null && p.CompareAtPrice > p.Price);
        if (query.InStockOnly == true) q = q.Where(p => p.StockQuantity - p.ReservedQuantity > 0);
        if (query.PaymentMode.HasValue)
        {
            var mode = query.PaymentMode.Value;
            q = q.Where(p => p.PaymentMode == mode || p.PaymentMode == StorePaymentMode.Both);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(p => p.Name.ToLower().Contains(term) ||
                             p.Sku.ToLower().Contains(term) ||
                             (p.Description != null && p.Description.ToLower().Contains(term)));
        }

        q = query.SortBy?.ToLowerInvariant() switch
        {
            "price" => query.SortDescending ? q.OrderByDescending(p => p.Price) : q.OrderBy(p => p.Price),
            "name" => query.SortDescending ? q.OrderByDescending(p => p.Name) : q.OrderBy(p => p.Name),
            "popular" => q.OrderByDescending(p => p.SoldQuantity),
            "discount" => q.OrderByDescending(p =>
                p.CompareAtPrice != null && p.CompareAtPrice > p.Price
                    ? (p.CompareAtPrice.Value - p.Price) / p.CompareAtPrice.Value
                    : 0),
            _ => q.OrderByDescending(p => p.CreatedAt)
        };

        var total = await q.LongCountAsync(ct);
        var items = await q.Skip(query.Skip).Take(query.PageSize).Select(Projection).ToListAsync(ct);

        return PagedResult<StoreProductListItemDto>.Create(items, query.Page, query.PageSize, total);
    }

    public async Task<StoreProductDetailDto> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await _db.StoreProducts
            .AsNoTracking()
            .Include(p => p.Images)
            .Include(p => p.Category).ThenInclude(c => c!.Parent)
            .FirstOrDefaultAsync(p => p.Id == productId, ct)
            ?? throw new DomainException(ErrorCodes.ProductNotFound, "Product not found.");

        if (!product.IsPubliclyVisible)
        {
            throw new DomainException(ErrorCodes.ProductNotAvailable, "This product is not available.");
        }

        return MapDetail(product);
    }

    internal static StoreProductDetailDto MapDetail(StoreProduct p) => new(
        p.Id, p.Name, p.Slug, p.Description, p.Sku, p.CategoryId, p.Category?.Name,
        p.Price, p.CompareAtPrice, p.Currency, p.StockQuantity, p.ReservedQuantity, p.AvailableQuantity,
        p.SoldQuantity, p.AvailableQuantity > 0, p.Status, p.PaymentMode,
        p.PaymentMode is StorePaymentMode.ShoppingWallet or StorePaymentMode.Both,
        p.PaymentMode is StorePaymentMode.MainWallet or StorePaymentMode.Both,
        p.IsFeatured, p.RequiresShipping, p.ShippingFee,
        p.Images.OrderBy(i => i.DisplayOrder)
            .Select(i => new StoreProductImageDto(i.Id, i.Url, i.ThumbnailUrl, i.IsPrimary, i.DisplayOrder))
            .ToList(),
        p.CreatedAt, p.UpdatedAt,
        p.Category?.ParentId, p.Category?.Parent?.Name);

    public async Task<IReadOnlyList<StoreCategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        const string cacheKey = "store:categories:public";

        var cached = await _cache.GetAsync<List<StoreCategoryDto>>(cacheKey, ct);
        if (cached is not null) return cached;

        // Both levels in one list; a sub-department carries its parent's id. One whose main
        // department is hidden is hidden with it.
        var categories = await _db.StoreCategories
            .AsNoTracking()
            .Where(c => c.Status == StoreCategoryStatus.Active &&
                        (c.ParentId == null || c.Parent!.Status == StoreCategoryStatus.Active))
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .Select(c => new StoreCategoryDto(
                c.Id, c.Name, c.Slug, c.Description, c.ImageUrl, c.DisplayOrder, c.Status,
                c.Products.Count(p => p.Status == StoreProductStatus.Active) +
                c.Children
                    .Where(ch => ch.Status == StoreCategoryStatus.Active)
                    .SelectMany(ch => ch.Products)
                    .Count(p => p.Status == StoreProductStatus.Active),
                c.ParentId,
                c.Parent != null ? c.Parent.Name : null))
            .ToListAsync(ct);

        // Categories are read-heavy and change rarely, so this is one of the few safe caches.
        await _cache.SetAsync(cacheKey, categories, TimeSpan.FromMinutes(10), ct);
        return categories;
    }

    public async Task<StoreCategoryDto> GetCategoryAsync(Guid categoryId, CancellationToken ct = default)
    {
        var category = await _db.StoreCategories
            .AsNoTracking()
            .Where(c => c.Id == categoryId)
            .Select(c => new StoreCategoryDto(
                c.Id, c.Name, c.Slug, c.Description, c.ImageUrl, c.DisplayOrder, c.Status,
                c.Products.Count(p => p.Status == StoreProductStatus.Active) +
                c.Children
                    .Where(ch => ch.Status == StoreCategoryStatus.Active)
                    .SelectMany(ch => ch.Products)
                    .Count(p => p.Status == StoreProductStatus.Active),
                c.ParentId,
                c.Parent != null ? c.Parent.Name : null))
            .FirstOrDefaultAsync(ct);

        return category ?? throw new DomainException(ErrorCodes.CategoryNotFound, "Category not found.");
    }
}
