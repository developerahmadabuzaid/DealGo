using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Store;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionPlatform.Application.M5azn;

/// <summary>A product imported from M5azn, as the M5azn page lists it.</summary>
public sealed record M5aznImportedProductDto(
    Guid Id,
    string Name,
    string Sku,
    string? M5aznSku,
    long M5aznProductId,
    string? CategoryName,
    string? ImageUrl,
    int ImageCount,
    decimal Price,
    decimal? M5aznCostPrice,
    int Stock,
    StoreProductStatus Status,
    DateTime? M5aznSyncedAt,
    DateTime? M5aznRemovedAt);

/// <summary>
/// What one import did. <c>Problems</c> are in words, for the dashboard: the first import with the
/// real seller account is where M5azn's undocumented answers meet this code.
/// </summary>
public sealed record M5aznImportSummary(
    DateTime At,
    int Found,
    int Created,
    int Updated,
    int Unchanged,
    int Hidden,
    int Skipped,
    IReadOnlyList<string> Problems)
{
    public string Describe() =>
        $"{Found} found: {Created} new, {Updated} updated, {Unchanged} unchanged, {Hidden} hidden, {Skipped} skipped." +
        (Problems.Count > 0 ? " " + string.Join(" | ", Problems) : string.Empty);
}

public sealed class M5aznProductQuery : PagedQuery;

/// <summary>
/// The products of the seller's M5azn shop, imported as store products of their own
/// (<see cref="StoreProductSource.M5azn"/>) and kept in step with M5azn: name, description, every
/// picture, category, stock, and price (M5azn's plus the profit percentage). Products staff add in
/// the dashboard are never touched here and never go to M5azn.
/// </summary>
public interface IM5aznCatalogService
{
    /// <summary>Reads the seller's list (read-only) and creates, updates or hides the imported products.</summary>
    Task<M5aznImportSummary> ImportAsync(CancellationToken ct = default);

    Task<PagedResult<M5aznImportedProductDto>> GetImportedProductsAsync(M5aznProductQuery query, CancellationToken ct = default);

    /// <summary>Re-prices imported products from the M5azn price already known. No call to M5azn.</summary>
    Task<int> ApplyProfitAsync(decimal profitPercentage, CancellationToken ct = default);
}

public sealed class M5aznCatalogService : IM5aznCatalogService
{
    private const int MaxPages = 50;
    private const int MaxDetailCalls = 300;
    private const int MaxProblems = 12;

    private readonly IApplicationDbContext _db;
    private readonly IM5aznClient _client;
    private readonly IM5aznAccountProvider _account;
    private readonly IAuditService _audit;
    private readonly ICacheService _cache;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<M5aznCatalogService> _logger;

    public M5aznCatalogService(
        IApplicationDbContext db,
        IM5aznClient client,
        IM5aznAccountProvider account,
        IAuditService audit,
        ICacheService cache,
        IDateTimeProvider clock,
        ILogger<M5aznCatalogService> logger)
    {
        _db = db;
        _client = client;
        _account = account;
        _audit = audit;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    // ================================================================ import

    public async Task<M5aznImportSummary> ImportAsync(CancellationToken ct = default)
    {
        var account = await _account.RefreshAsync(ct: ct);
        if (!account.IsActive || !_client.IsEnabled)
        {
            throw new DomainException(ErrorCodes.ServiceUnavailable,
                "M5azn is switched off, or its seller account is missing. Set both on the M5azn page.");
        }

        var problems = new List<string>();
        void Problem(string text)
        {
            if (problems.Count < MaxProblems) problems.Add(text);
        }

        // ---- read the whole list first, so a failure half-way changes nothing
        var found = new Dictionary<long, M5aznCatalogProduct>();
        for (var page = 1; page <= MaxPages; page++)
        {
            M5aznProductPage result;
            try
            {
                result = await _client.GetMyProductsAsync(page, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "M5azn: the product list could not be read (page {Page}).", page);
                throw new DomainException(ErrorCodes.ServiceUnavailable,
                    $"M5azn's product list could not be read: {ex.Message}. Nothing was changed.");
            }

            if (result.Unreadable)
            {
                var stopped = new M5aznImportSummary(_clock.UtcNow, 0, 0, 0, 0, 0, 0,
                    [result.Problem ?? "M5azn's product list could not be read.", "Nothing was changed."]);
                await RecordAsync(stopped, account, ct);
                return stopped;
            }

            if (result.Problem is not null) Problem(result.Problem);
            foreach (var product in result.Products) found.TryAdd(product.ProductId, product);
            if (!result.HasMore || result.Products.Count == 0) break;
        }

        // ---- fill in what the list leaves out from the product's details
        var detailCalls = 0;
        foreach (var id in found.Keys.ToList())
        {
            var item = found[id];
            if (item.Images.Count >= 2 && item.Description is not null && item.Price is not null && item.Sku is not null) continue;
            if (detailCalls++ >= MaxDetailCalls) break;

            try
            {
                if (await _client.GetProductAsync(id, ct) is { } details) found[id] = Merge(item, details);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "M5azn: details of product {Id} could not be read.", id);
                Problem($"Details of M5azn product {id} could not be read ({ex.Message}); the list's own fields were used.");
            }
        }

        // ---- apply
        var now = _clock.UtcNow;
        var existing = await _db.StoreProducts
            .Include(p => p.Images)
            .Where(p => p.Source == StoreProductSource.M5azn)
            .ToListAsync(ct);
        var byRemoteId = existing.Where(p => p.M5aznProductId != null).ToDictionary(p => p.M5aznProductId!.Value);

        var takenSkus = new HashSet<string>(
            await _db.StoreProducts.Select(p => p.Sku).ToListAsync(ct), StringComparer.OrdinalIgnoreCase);
        var categories = await _db.StoreCategories.ToListAsync(ct);
        var takenCategorySlugs = new HashSet<string>(categories.Select(c => c.Slug), StringComparer.OrdinalIgnoreCase);

        int created = 0, updated = 0, unchanged = 0, skipped = 0;
        foreach (var item in found.Values)
        {
            if (string.IsNullOrWhiteSpace(item.Sku))
            {
                skipped++;
                Problem($"M5azn product {item.ProductId} ({item.Name ?? "no name"}) has no SKU, which M5azn needs on every order. Skipped.");
                continue;
            }

            if (item.Price is not > 0)
            {
                skipped++;
                Problem($"M5azn product {item.ProductId} ({item.Name ?? "no name"}) has no price. Skipped: nothing is sold without one.");
                continue;
            }

            var isNew = !byRemoteId.TryGetValue(item.ProductId, out var product);
            if (product is null)
            {
                product = new StoreProduct
                {
                    Source = StoreProductSource.M5azn,
                    M5aznProductId = item.ProductId,
                    Sku = SkuFor(item, takenSkus),
                    PaymentMode = StorePaymentMode.Both,
                    RequiresShipping = true,
                    Status = StoreProductStatus.Active,
                    CreatedAt = now
                };
                _db.StoreProducts.Add(product);
                byRemoteId[item.ProductId] = product;
            }

            var changed = Apply(product, item, account, categories, takenCategorySlugs, now, isNew);
            product.M5aznSyncedAt = now;

            if (isNew) created++;
            else if (changed) updated++;
            else unchanged++;
        }

        // ---- products M5azn no longer lists are hidden, never deleted (orders may point at them)
        var hidden = 0;
        foreach (var product in existing.Where(p => p.M5aznProductId is { } id && !found.ContainsKey(id) && p.M5aznRemovedAt is null))
        {
            product.M5aznRemovedAt = now;
            if (product.Status is StoreProductStatus.Active or StoreProductStatus.OutOfStock)
            {
                product.Status = StoreProductStatus.Hidden;
            }
            product.UpdatedAt = now;
            hidden++;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(ErrorCodes.Conflict,
                "Some M5azn products were changed meanwhile. Nothing was saved; import again.");
        }

        var summary = new M5aznImportSummary(now, found.Count, created, updated, unchanged, hidden, skipped, problems);
        await RecordAsync(summary, account, ct);
        await _cache.RemoveByPrefixAsync("store:", ct);

        _logger.LogInformation("M5azn import: {Summary}", summary.Describe());
        return summary;
    }

    /// <summary>The list's fields, with what only the details have filled in. Pictures from both.</summary>
    internal static M5aznCatalogProduct Merge(M5aznCatalogProduct list, M5aznCatalogProduct details) => list with
    {
        Sku = list.Sku ?? details.Sku,
        Name = list.Name ?? details.Name,
        Description = list.Description ?? details.Description,
        Price = list.Price ?? details.Price,
        Quantity = list.Quantity ?? details.Quantity,
        Images = list.Images.Concat(details.Images).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList(),
        CategoryName = list.CategoryName ?? details.CategoryName,
        DeliveryTime = list.DeliveryTime ?? details.DeliveryTime
    };

    /// <summary>Copies M5azn's product onto the store product. True when anything changed.</summary>
    private bool Apply(
        StoreProduct product,
        M5aznCatalogProduct item,
        M5aznAccount account,
        List<StoreCategory> categories,
        HashSet<string> takenCategorySlugs,
        DateTime now,
        bool isNew)
    {
        var changed = false;
        void Set<T>(T current, T value, Action<T> assign)
        {
            if (EqualityComparer<T>.Default.Equals(current, value)) return;
            assign(value);
            changed = true;
        }

        var name = Clip(item.Name, 256) ?? $"M5azn {item.ProductId}";
        Set(product.Name, name, v => product.Name = v);
        if (isNew) product.Slug = Clip($"{StoreAdminService.Slugify(name)}-m5-{item.ProductId}".Trim('-'), 300)!;

        if (Clip(item.Description, 8000) is { } description) Set(product.Description, description, v => product.Description = v);
        Set(product.M5aznSku, Clip(item.Sku!.Trim(), 128), v => product.M5aznSku = v);
        Set(product.DeliveryTime, Clip(item.DeliveryTime, 64) ?? account.DeliveryTime, v => product.DeliveryTime = v);

        if (Clip(item.CategoryName, 128) is { } categoryName)
        {
            var category = CategoryFor(categoryName, categories, takenCategorySlugs, now);
            if (product.CategoryId != category.Id)
            {
                product.CategoryId = category.Id;
                changed = true;
            }
        }

        // Price: M5azn's plus the profit percentage.
        var cost = item.Price!.Value;
        Set(product.M5aznCostPrice, cost, v => product.M5aznCostPrice = v);
        Set(product.Price, M5aznPricing.PriceFor(cost, account.ProfitPercentage), v => product.Price = v);

        // Stock, with an inventory record for every change.
        var quantity = Math.Max(0, item.Quantity ?? 0);
        if (isNew || product.StockQuantity != quantity)
        {
            var before = isNew ? 0 : product.StockQuantity;
            product.StockQuantity = quantity;
            _db.InventoryTransactions.Add(new InventoryTransaction
            {
                Product = product,
                Type = isNew ? InventoryTransactionType.Initial
                    : quantity > before ? InventoryTransactionType.Restock : InventoryTransactionType.Adjustment,
                Quantity = quantity - before,
                BeforeQuantity = before,
                AfterQuantity = quantity,
                ReferenceType = "M5azn",
                Reason = isNew ? "Imported from M5azn" : "Stock synced from M5azn",
                CreatedAt = now
            });
            changed = true;
        }

        // Shown again if M5azn lists it again; a product staff set to draft stays a draft.
        var status = product.Status;
        if (product.M5aznRemovedAt is not null)
        {
            product.M5aznRemovedAt = null;
            if (status == StoreProductStatus.Hidden) status = StoreProductStatus.Active;
            changed = true;
        }

        if (status is StoreProductStatus.Active or StoreProductStatus.OutOfStock)
        {
            status = quantity > 0 ? StoreProductStatus.Active : StoreProductStatus.OutOfStock;
        }

        Set(product.Status, status, v => product.Status = v);

        // Pictures: replaced as a set when M5azn's differ, main one first.
        var wanted = item.Images.Where(u => u.Length <= 1024).ToList();
        var current = product.Images.OrderBy(i => i.DisplayOrder).Select(i => i.Url).ToList();
        if (wanted.Count > 0 && !wanted.SequenceEqual(current, StringComparer.Ordinal))
        {
            foreach (var old in product.Images.ToList())
            {
                _db.StoreProductImages.Remove(old);
                product.Images.Remove(old);
            }

            for (var i = 0; i < wanted.Count; i++)
            {
                product.Images.Add(new StoreProductImage
                {
                    Url = wanted[i], IsPrimary = i == 0, DisplayOrder = i, CreatedAt = now
                });
            }

            changed = true;
        }

        if (changed) product.UpdatedAt = now;
        return changed;
    }

    /// <summary>M5azn's own SKU when no other product has it, else one made from the M5azn id.</summary>
    private static string SkuFor(M5aznCatalogProduct item, HashSet<string> taken)
    {
        var own = item.Sku!.Trim();
        var sku = own.Length <= 64 && !taken.Contains(own) ? own : $"M5AZN-{item.ProductId}";
        taken.Add(sku);
        return sku;
    }

    /// <summary>The store category of that name, made (as a main department) when there is none.</summary>
    private StoreCategory CategoryFor(string name, List<StoreCategory> categories, HashSet<string> takenSlugs, DateTime now)
    {
        var match = categories.FirstOrDefault(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        var slug = StoreAdminService.Slugify(name);
        if (slug.Length == 0) slug = "m5azn";
        slug = Clip(slug, 150)!;
        var candidate = slug;
        for (var n = 2; takenSlugs.Contains(candidate); n++) candidate = $"{slug}-{n}";
        takenSlugs.Add(candidate);

        var category = new StoreCategory
        {
            Name = name,
            Slug = candidate,
            Status = StoreCategoryStatus.Active,
            DisplayOrder = 100,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.StoreCategories.Add(category);
        categories.Add(category);
        return category;
    }

    private async Task RecordAsync(M5aznImportSummary summary, M5aznAccount account, CancellationToken ct)
    {
        var row = await _db.M5aznSettings.OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            // First record: carry over the settings in effect, so saving one changes nothing else.
            row = new M5aznSettings
            {
                Enabled = account.Enabled,
                AutoSubmit = account.AutoSubmit,
                ProfitPercentage = account.ProfitPercentage,
                DeliveryTime = account.DeliveryTime,
                CreatedAt = summary.At
            };
            _db.M5aznSettings.Add(row);
        }

        row.LastImportAt = summary.At;
        row.LastImportSummary = Clip(summary.Describe(), 4000);
        row.UpdatedAt = summary.At;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.M5aznProductsImported, nameof(StoreProduct), null, null, new
        {
            summary.Found, summary.Created, summary.Updated, summary.Hidden, summary.Skipped, Problems = summary.Problems.Count
        }, ct: ct);
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length <= max ? v : v[..max].TrimEnd();
    }

    // ================================================================ listing and prices

    public async Task<PagedResult<M5aznImportedProductDto>> GetImportedProductsAsync(M5aznProductQuery query, CancellationToken ct = default)
    {
        var q = _db.StoreProducts.AsNoTracking().Where(p => p.Source == StoreProductSource.M5azn && p.M5aznProductId != null);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            long.TryParse(term, out var remoteId);
            q = q.Where(p => p.Name.ToLower().Contains(term) || p.Sku.ToLower().Contains(term)
                             || (p.M5aznSku != null && p.M5aznSku.ToLower().Contains(term))
                             || p.M5aznProductId == remoteId);
        }

        var total = await q.LongCountAsync(ct);
        var rows = await q
            .OrderBy(p => p.M5aznRemovedAt != null).ThenBy(p => p.Name)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(p => new M5aznImportedProductDto(
                p.Id, p.Name, p.Sku, p.M5aznSku, p.M5aznProductId!.Value,
                p.Category != null ? p.Category.Name : null,
                p.Images.OrderBy(i => i.DisplayOrder).Select(i => i.Url).FirstOrDefault(),
                p.Images.Count,
                p.Price, p.M5aznCostPrice, p.StockQuantity, p.Status, p.M5aznSyncedAt, p.M5aznRemovedAt))
            .ToListAsync(ct);

        return PagedResult<M5aznImportedProductDto>.Create(rows, query.Page, query.PageSize, total);
    }

    public async Task<int> ApplyProfitAsync(decimal profitPercentage, CancellationToken ct = default)
    {
        var products = await _db.StoreProducts
            .Where(p => p.Source == StoreProductSource.M5azn && p.M5aznCostPrice != null)
            .ToListAsync(ct);

        var now = _clock.UtcNow;
        var updated = 0;
        foreach (var product in products)
        {
            var price = M5aznPricing.PriceFor(product.M5aznCostPrice!.Value, profitPercentage);
            if (product.Price == price) continue;
            product.Price = price;
            product.UpdatedAt = now;
            updated++;
        }

        if (updated == 0) return 0;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(ErrorCodes.Conflict,
                "Some M5azn products were changed meanwhile. Save the profit percentage again.");
        }

        await _cache.RemoveByPrefixAsync("store:", ct);
        return updated;
    }
}
