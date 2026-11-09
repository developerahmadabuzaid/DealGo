using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Store;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    /// <summary>This product's own profit percentage; null when the general one applies.</summary>
    decimal? ProfitPercentage,
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

/// <summary>Null: the product goes back to the general profit percentage.</summary>
public sealed record M5aznProductProfitRequest(decimal? ProfitPercentage);

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

    /// <summary>
    /// Re-prices imported products that use the general profit percentage, from the M5azn price
    /// already known. Products with their own percentage keep theirs. No call to M5azn.
    /// </summary>
    Task<int> ApplyProfitAsync(decimal profitPercentage, CancellationToken ct = default);

    /// <summary>
    /// Gives one imported product its own profit percentage, or (null) returns it to the general
    /// one, and re-prices it at once. No call to M5azn.
    /// </summary>
    Task<M5aznImportedProductDto> SetProductProfitAsync(Guid productId, decimal? profitPercentage, CancellationToken ct = default);
}

public sealed class M5aznCatalogService : IM5aznCatalogService
{
    /// <summary>A stop for a list that never ends. At 25 a page this is 10,000 products.</summary>
    private const int MaxPages = 400;

    /// <summary>M5azn's product types, from its documentation; "local" is what it lists when none is named.</summary>
    private static readonly string[] ProductTypes = ["local", "global", "ali_express", "digital"];
    private const int MaxDetailCalls = 300;
    private const int MaxProblems = 12;
    private const int MaxCategoryCalls = 150;

    /// <summary>Pictures of M5azn's categories by name, for the store categories an import makes.</summary>
    private Dictionary<string, string> _categoryImages = new(StringComparer.OrdinalIgnoreCase);

    private readonly IApplicationDbContext _db;
    private readonly IM5aznClient _client;
    private readonly IM5aznAccountProvider _account;
    private readonly IAuditService _audit;
    private readonly ICacheService _cache;
    private readonly IDateTimeProvider _clock;
    private readonly IDistributedLockService _locks;
    private readonly M5aznOptions _options;
    private readonly ILogger<M5aznCatalogService> _logger;

    public M5aznCatalogService(
        IApplicationDbContext db,
        IM5aznClient client,
        IM5aznAccountProvider account,
        IAuditService audit,
        ICacheService cache,
        IDateTimeProvider clock,
        IDistributedLockService locks,
        IOptions<M5aznOptions> options,
        ILogger<M5aznCatalogService> logger)
    {
        _db = db;
        _client = client;
        _account = account;
        _audit = audit;
        _cache = cache;
        _clock = clock;
        _locks = locks;
        _options = options.Value;
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

        // One import at a time, across servers: the button and the hourly sync otherwise change the
        // same products at once, and one of them is refused.
        await using var importLock = await _locks.AcquireAsync("m5azn:import", TimeSpan.FromMinutes(45), ct);
        if (importLock is null)
        {
            throw new DomainException(ErrorCodes.Conflict,
                "An import from M5azn is already running (it also runs every hour on its own). Try again in a minute.");
        }

        var problems = new List<string>();
        void Problem(string text)
        {
            if (problems.Count < MaxProblems) problems.Add(text);
        }

        // ---- read the whole list first, so a failure half-way changes nothing
        //
        // M5azn's list answers with its "local" products only, unless another product type is
        // named: a seller with 320 products saw 9 come in. Every type is asked for in turn. The
        // local list must be readable; the other types are extra, so one that fails is noted and
        // the import goes on with the rest.
        var found = new Dictionary<long, M5aznCatalogProduct>();
        var perType = new List<string>();
        var listComplete = true;
        foreach (var type in ProductTypes)
        {
            var required = type == "local";
            var before = found.Count;
            var listed = 0;

            for (var page = 1; page <= MaxPages; page++)
            {
                if (page > 1 && _options.CatalogRequestDelayMs > 0)
                {
                    await Task.Delay(Math.Min(_options.CatalogRequestDelayMs, 10_000), ct);
                }

                M5aznProductPage result;
                try
                {
                    result = await _client.GetMyProductsAsync(page, ct: ct, productType: required ? null : type);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "M5azn: the {Type} product list could not be read (page {Page}).", type, page);
                    if (!required)
                    {
                        Problem($"M5azn's {type} products could not be read: {ex.Message}");
                        listComplete = false;
                        break;
                    }

                    throw new DomainException(ErrorCodes.ServiceUnavailable,
                        $"M5azn's product list could not be read: {ex.Message}. Nothing was changed.");
                }

                if (result.Unreadable)
                {
                    if (!required)
                    {
                        Problem($"M5azn's {type} products could not be read: {result.Problem}");
                        listComplete = false;
                        break;
                    }

                    var stopped = new M5aznImportSummary(_clock.UtcNow, 0, 0, 0, 0, 0, 0,
                        [result.Problem ?? "M5azn's product list could not be read.", "Nothing was changed."]);
                    await RecordAsync(stopped, account, ct);
                    return stopped;
                }

                if (result.Problem is not null) Problem(result.Problem);
                listed += result.Products.Count;
                foreach (var product in result.Products) found.TryAdd(product.ProductId, product);
                if (!result.HasMore || result.Products.Count == 0) break;

                // Products past the last page read are not gone, so nothing may be hidden for
                // being missing from this list.
                if (page == MaxPages)
                {
                    listComplete = false;
                    _logger.LogWarning("M5azn: the {Type} list is longer than {Pages} pages; the rest was not read.", type, MaxPages);
                    Problem($"M5azn's {type} list is longer than {MaxPages} pages; the products after that were not read.");
                }
            }

            perType.Add($"{type} {found.Count - before}");
            _logger.LogInformation("M5azn: {Type} list gave {Listed} item(s), {New} new.", type, listed, found.Count - before);
        }

        _logger.LogInformation("M5azn: product list read, {Total} product(s): {PerType}.", found.Count, string.Join(", ", perType));

        // ---- fill in what the list leaves out from the product's details: only for products the
        // store does not have complete yet, so an hourly sync of known products asks nothing more.
        var stored = (await _db.StoreProducts.AsNoTracking()
                .Where(p => p.Source == StoreProductSource.M5azn && p.M5aznProductId != null)
                .Select(p => new { Id = p.M5aznProductId!.Value, Pictures = p.Images.Count, HasDescription = p.Description != null })
                .ToListAsync(ct))
            .ToDictionary(p => p.Id);

        var detailed = new HashSet<long>();
        var detailCalls = 0;
        foreach (var id in found.Keys.ToList())
        {
            var item = found[id];
            var complete = stored.TryGetValue(id, out var known) && known.Pictures >= 2 && known.HasDescription;
            var listIsEnough = item.Images.Count >= 2 && item.Description is not null && item.Price is not null && item.Sku is not null;
            if (complete || listIsEnough) continue;
            if (detailCalls >= MaxDetailCalls) break;

            try
            {
                if (detailCalls++ > 0 && _options.CatalogRequestDelayMs > 0)
                {
                    await Task.Delay(Math.Min(_options.CatalogRequestDelayMs, 10_000), ct);
                }

                if (await _client.GetProductAsync(id, ct) is { } details)
                {
                    found[id] = Merge(item, details);
                    detailed.Add(id);
                }
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

        // ---- categories: M5azn's list rarely names them, so they are looked up
        var placed = await _db.StoreProducts.AsNoTracking()
            .Where(p => p.Source == StoreProductSource.M5azn && p.CategoryId != null && p.M5aznProductId != null)
            .Select(p => p.M5aznProductId!.Value)
            .ToListAsync(ct);
        await AssignCategoriesAsync(found, placed.ToHashSet(), Problem, ct);

        // ---- apply; once more from fresh data if a product was changed meanwhile (staff editing
        // it, say): what M5azn said is still in hand, so it is not asked again.
        var now = _clock.UtcNow;
        var problemsBefore = problems.Count;
        (int Created, int Updated, int Unchanged, int Hidden, int Skipped) counts;
        try
        {
            counts = await ApplyAllAsync(found, detailed, account, Problem, now, listComplete, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogInformation("M5azn import: products were changed meanwhile; applying once more from fresh data.");
            ForgetTracked();
            problems.RemoveRange(problemsBefore, problems.Count - problemsBefore);

            try
            {
                counts = await ApplyAllAsync(found, detailed, account, Problem, now, listComplete, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                ForgetTracked();
                throw new DomainException(ErrorCodes.Conflict,
                    "Some M5azn products were being changed at the same time. Nothing was saved; import again in a minute.");
            }
        }

        var (created, updated, unchanged, hidden, skipped) = counts;

        var summary = new M5aznImportSummary(now, found.Count, created, updated, unchanged, hidden, skipped, problems);
        await RecordAsync(summary, account, ct);
        await _cache.RemoveByPrefixAsync("store:", ct);

        _logger.LogInformation("M5azn import: {Summary}", summary.Describe());
        return summary;
    }

    /// <summary>Writes what M5azn said onto the store's imported products, in one save.</summary>
    private async Task<(int Created, int Updated, int Unchanged, int Hidden, int Skipped)> ApplyAllAsync(
        Dictionary<long, M5aznCatalogProduct> found,
        HashSet<long> detailed,
        M5aznAccount account,
        Action<string> Problem,
        DateTime now,
        bool listComplete,
        CancellationToken ct)
    {
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

            // The list has one picture only: it never shrinks a gallery the details filled in.
            var fullGallery = isNew || detailed.Contains(item.ProductId) || item.Images.Count >= product.Images.Count;
            var changed = Apply(product, item, account, categories, takenCategorySlugs, now, isNew, fullGallery);
            product.M5aznSyncedAt = now;

            if (isNew) created++;
            else if (changed) updated++;
            else unchanged++;
        }

        // ---- products M5azn no longer lists are hidden, never deleted (orders may point at them).
        // Only when the whole list was read: a product type that could not be read says nothing
        // about whether its products are still there.
        var hidden = 0;
        foreach (var product in existing.Where(p => listComplete && p.M5aznProductId is { } id && !found.ContainsKey(id) && p.M5aznRemovedAt is null))
        {
            product.M5aznRemovedAt = now;
            if (product.Status is StoreProductStatus.Active or StoreProductStatus.OutOfStock)
            {
                product.Status = StoreProductStatus.Hidden;
            }
            product.UpdatedAt = now;
            hidden++;
        }

        await _db.SaveChangesAsync(ct);
        return (created, updated, unchanged, hidden, skipped);
    }

    private void ForgetTracked()
    {
        if (_db is DbContext context) context.ChangeTracker.Clear();
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
        CategoryId = list.CategoryId ?? details.CategoryId,
        ParentCategoryName = list.ParentCategoryName ?? details.ParentCategoryName,
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
        bool isNew,
        bool fullGallery = true)
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
            var category = CategoryFor(categoryName, Clip(item.ParentCategoryName, 128), categories, takenCategorySlugs, now);
            if (product.CategoryId != category.Id)
            {
                product.CategoryId = category.Id;
                changed = true;
            }
        }

        // Price: M5azn's plus the profit percentage.
        var cost = item.Price!.Value;
        Set(product.M5aznCostPrice, cost, v => product.M5aznCostPrice = v);
        Set(product.Price, M5aznPricing.PriceFor(cost, product.M5aznProfitPercentage ?? account.ProfitPercentage),
            v => product.Price = v);

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
        if (wanted.Count > 0 && fullGallery && !wanted.SequenceEqual(current, StringComparer.Ordinal))
        {
            foreach (var old in product.Images.ToList())
            {
                _db.StoreProductImages.Remove(old);
                product.Images.Remove(old);
            }

            // Added to the set itself, as everywhere else: an image already carries its new id, and
            // one found only through an existing product's collection would be taken for a row to
            // update, which matches nothing ("changed meanwhile").
            for (var i = 0; i < wanted.Count; i++)
            {
                var image = new StoreProductImage
                {
                    ProductId = product.Id, Product = product, Url = wanted[i], IsPrimary = i == 0, DisplayOrder = i, CreatedAt = now
                };
                _db.StoreProductImages.Add(image);
                product.Images.Add(image);
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

    /// <summary>
    /// The store category for M5azn's category: found by name, or made, under its main category
    /// when M5azn has one (the store has two levels). M5azn's picture is used when the store
    /// category has none.
    /// </summary>
    private StoreCategory CategoryFor(
        string name, string? parentName, List<StoreCategory> categories, HashSet<string> takenSlugs, DateTime now)
    {
        StoreCategory? parent = null;
        if (parentName is not null && !string.Equals(parentName, name, StringComparison.OrdinalIgnoreCase))
        {
            parent = CategoryFor(parentName, null, categories, takenSlugs, now);
            if (parent.ParentId is not null) parent = null;   // a sub-department cannot have its own
        }

        static bool Same(StoreCategory c, string n) => string.Equals(c.Name.Trim(), n, StringComparison.OrdinalIgnoreCase);

        var match = (parent is null ? null : categories.FirstOrDefault(c => Same(c, name) && c.ParentId == parent.Id))
                    ?? categories.FirstOrDefault(c => Same(c, name));
        if (match is not null)
        {
            if (match.ImageUrl is null && _categoryImages.TryGetValue(name, out var picture) && picture.Length <= 1024)
            {
                match.ImageUrl = picture;
                match.UpdatedAt = now;
            }
            return match;
        }

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
            ParentId = parent?.Id,
            ImageUrl = _categoryImages.TryGetValue(name, out var image) && image.Length <= 1024 ? image : null,
            Status = StoreCategoryStatus.Active,
            DisplayOrder = 100,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.StoreCategories.Add(category);
        categories.Add(category);
        return category;
    }

    /// <summary>
    /// Fills in each product's category from M5azn's category list: by the category id a product
    /// carries, else by asking the product list one category at a time (it takes a
    /// <c>category_id</c>). Products already in a store category are not asked about again.
    /// </summary>
    private async Task AssignCategoriesAsync(
        Dictionary<long, M5aznCatalogProduct> found, HashSet<long> alreadyPlaced, Action<string> problem, CancellationToken ct)
    {
        _categoryImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Nothing to look up when every product is already in a store category: then no request at all.
        if (found.Values.All(p => alreadyPlaced.Contains(p.ProductId)))
        {
            foreach (var id in found.Keys.ToList()) found[id] = found[id] with { CategoryName = null, ParentCategoryName = null };
            return;
        }

        M5aznCategoryList list;
        try
        {
            list = await _client.GetCategoriesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "M5azn: the category list could not be read.");
            problem($"M5azn's categories could not be read ({ex.Message}); products keep the category they had.");
            return;
        }

        if (list.Problem is not null) problem(list.Problem);
        if (list.Categories.Count == 0) return;

        var byId = list.Categories.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var c in list.Categories)
        {
            if (c.ImageUrl is not null) _categoryImages.TryAdd(c.Name.Trim(), c.ImageUrl);
        }

        string? ParentOf(M5aznCategory c) =>
            c.ParentId is { } p && p != c.Id && byId.TryGetValue(p, out var parent) ? parent.Name : null;

        void Place(long productId, M5aznCategory c) =>
            found[productId] = found[productId] with { CategoryName = c.Name, ParentCategoryName = ParentOf(c) };

        // 1. By the category id or name the product carries (M5azn's list gives category_id on
        //    every product): no further request. A product the store already has in a category
        //    keeps it, so a category staff chose is never overwritten.
        foreach (var item in found.Values.ToList())
        {
            if (alreadyPlaced.Contains(item.ProductId))
            {
                found[item.ProductId] = item with { CategoryName = null, ParentCategoryName = null };
                continue;
            }

            if (item.CategoryName is null && item.CategoryId is { } cid && byId.TryGetValue(cid, out var c))
            {
                Place(item.ProductId, c);
            }
            else if (item.CategoryName is not null && item.ParentCategoryName is null
                     && list.Categories.FirstOrDefault(x => string.Equals(x.Name, item.CategoryName, StringComparison.OrdinalIgnoreCase)) is { } named
                     && ParentOf(named) is { } parentName)
            {
                found[item.ProductId] = item with { ParentCategoryName = parentName };
            }
        }

        // Products in no store category yet, and none named by M5azn's list.
        var missing = found.Values
            .Where(p => p.CategoryName is null && !alreadyPlaced.Contains(p.ProductId))
            .Select(p => p.ProductId)
            .ToHashSet();
        if (missing.Count == 0) return;

        var calls = 0;
        async Task PaceAsync()
        {
            // Paced: M5azn answers 429 when questions come too fast.
            if (calls++ > 0 && _options.CatalogRequestDelayMs > 0)
            {
                await Task.Delay(Math.Min(_options.CatalogRequestDelayMs, 10_000), ct);
            }
        }

        bool Unplaced(long id) => found[id].CategoryName is null;
        string? exampleShape = null;
        var placedAt = new Dictionary<long, long>();

        void PlaceAt(long productId, M5aznCategory c)
        {
            Place(productId, c);
            placedAt[productId] = c.Id;
        }

        try
        {
            // 2. The product's own details usually say its category.
            foreach (var id in missing.ToList())
            {
                if (calls >= MaxCategoryCalls) break;
                await PaceAsync();
                if (await _client.GetProductAsync(id, ct) is not { } details) continue;

                if (details.CategoryName is not null)
                {
                    var known = list.Categories.FirstOrDefault(x => string.Equals(x.Name, details.CategoryName, StringComparison.OrdinalIgnoreCase));
                    if (known is not null) PlaceAt(id, known);
                    else found[id] = found[id] with { CategoryName = details.CategoryName, ParentCategoryName = details.ParentCategoryName };
                }
                else if (details.CategoryId is { } cid && byId.TryGetValue(cid, out var byDetails))
                {
                    PlaceAt(id, byDetails);
                }

                if (Unplaced(id)) exampleShape ??= details.Shape;
            }

            if (missing.All(id => !Unplaced(id))) return;

            // 3. Else the list, one category at a time.
            async Task<IReadOnlyList<long>?> InCategoryAsync(long categoryId)
            {
                var ids = new List<long>();
                for (var page = 1; page <= 10 && calls < MaxCategoryCalls; page++)
                {
                    await PaceAsync();
                    var result = await _client.GetMyProductsAsync(page, categoryId, ct);
                    if (result.Unreadable) return null;
                    ids.AddRange(result.Products.Select(p => p.ProductId));
                    if (!result.HasMore || result.Products.Count == 0) break;
                }

                return ids;
            }

            // If M5azn ignored the filter, every product would land in the first category asked.
            // A category that cannot exist must come back empty before the answers are trusted.
            var nowhere = await InCategoryAsync(-1);
            if (nowhere is null || nowhere.Count > 0)
            {
                problem("M5azn's product list does not filter by category, so products without a category name were left as they were.");
                return;
            }

            int DepthOf(M5aznCategory c)
            {
                var depth = 0;
                for (var at = c; at.ParentId is { } p && byId.TryGetValue(p, out var parent) && depth < 5; at = parent) depth++;
                return depth;
            }

            bool IsAncestor(long ancestor, M5aznCategory c)
            {
                for (var (at, guard) = (c, 0); at.ParentId is { } p && byId.TryGetValue(p, out var parent) && guard < 5; at = parent, guard++)
                {
                    if (parent.Id == ancestor) return true;
                }
                return false;
            }

            // Main categories first, then deeper ones: a sub-category under a category that already
            // had products comes first, and the deepest category a product is found in wins. A main
            // category may list only its own products, not its sub-categories', so the search goes
            // on while a product has no category at all.
            var hit = new HashSet<long>();
            var ordered = list.Categories
                .OrderBy(DepthOf)
                .ToList();

            foreach (var c in ordered)
            {
                if (calls >= MaxCategoryCalls) break;

                var refines = c.ParentId is { } parentId && hit.Contains(parentId);
                if (!refines && missing.All(id => !Unplaced(id))) continue;

                var ids = await InCategoryAsync(c.Id);
                foreach (var id in ids ?? [])
                {
                    if (!missing.Contains(id)) continue;
                    if (Unplaced(id) || (placedAt.TryGetValue(id, out var at) && IsAncestor(at, c)))
                    {
                        PlaceAt(id, c);
                        hit.Add(c.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // What was sorted so far is kept; the rest are asked about on the next import.
            _logger.LogWarning(ex, "M5azn: products could not all be sorted into categories.");
            var left = missing.Count(id => found[id].CategoryName is null);
            problem($"M5azn stopped answering while products were being sorted into categories ({ex.Message}). " +
                    $"{missing.Count - left} placed; the other {left} are tried again on the next import.");
            return;
        }

        var unplaced = missing.Count(id => found[id].CategoryName is null);
        if (unplaced > 0)
        {
            problem($"{unplaced} product(s) were found in no M5azn category and have no category in the store." +
                    (exampleShape is null ? string.Empty : $" M5azn's product details have these fields: {exampleShape}"));
        }
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
        var rows = await Rows(q
            .OrderBy(p => p.M5aznRemovedAt != null).ThenBy(p => p.Name)
            .Skip(query.Skip).Take(query.PageSize))
            .ToListAsync(ct);

        return PagedResult<M5aznImportedProductDto>.Create(rows, query.Page, query.PageSize, total);
    }

    private static IQueryable<M5aznImportedProductDto> Rows(IQueryable<StoreProduct> q) =>
        q.Select(p => new M5aznImportedProductDto(
            p.Id, p.Name, p.Sku, p.M5aznSku, p.M5aznProductId!.Value,
            p.Category != null ? p.Category.Name : null,
            p.Images.OrderBy(i => i.DisplayOrder).Select(i => i.Url).FirstOrDefault(),
            p.Images.Count,
            p.Price, p.M5aznCostPrice, p.M5aznProfitPercentage, p.StockQuantity, p.Status, p.M5aznSyncedAt, p.M5aznRemovedAt));

    public async Task<M5aznImportedProductDto> SetProductProfitAsync(
        Guid productId, decimal? profitPercentage, CancellationToken ct = default)
    {
        if (profitPercentage is < 0 or > M5aznPricing.MaxProfitPercentage)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"The profit percentage must be between 0 and {M5aznPricing.MaxProfitPercentage:0}.");
        }

        var product = await _db.StoreProducts
            .FirstOrDefaultAsync(p => p.Id == productId && p.Source == StoreProductSource.M5azn, ct)
            ?? throw new DomainException(ErrorCodes.ProductNotFound, "Imported M5azn product not found.");

        var account = await _account.RefreshAsync(ct: ct);
        var own = profitPercentage is { } v ? Math.Round(v, 2) : (decimal?)null;
        var before = new { product.M5aznProfitPercentage, product.Price };

        product.M5aznProfitPercentage = own;
        if (product.M5aznCostPrice is { } cost)
        {
            product.Price = M5aznPricing.PriceFor(cost, own ?? account.ProfitPercentage);
        }
        product.UpdatedAt = _clock.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(ErrorCodes.Conflict, "The product was changed meanwhile. Try again.");
        }

        await _audit.LogAsync(AuditActionType.M5aznPricesUpdated, nameof(StoreProduct), productId, before,
            new { product.M5aznProfitPercentage, product.Price, GeneralProfitPercentage = account.ProfitPercentage }, ct: ct);
        await _cache.RemoveByPrefixAsync("store:", ct);

        return await Rows(_db.StoreProducts.AsNoTracking().Where(p => p.Id == productId)).FirstAsync(ct);
    }

    public async Task<int> ApplyProfitAsync(decimal profitPercentage, CancellationToken ct = default)
    {
        var products = await _db.StoreProducts
            .Where(p => p.Source == StoreProductSource.M5azn && p.M5aznCostPrice != null && p.M5aznProfitPercentage == null)
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
