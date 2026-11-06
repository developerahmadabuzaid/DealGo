using System.Linq.Expressions;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// Points the database's picture addresses at R2 once the pictures are there.
///
/// Every picture column holds either an address with its own host, which is left alone, or a path
/// on this site such as "/uploads/x.jpg" or "/catalog/x.webp". A path is rewritten to
/// "&lt;public base&gt;/uploads/x.jpg" only when that exact object is known to be in the bucket, so
/// nothing is ever pointed at a picture that is not there. Only the start of the address changes,
/// and the same path exists in both places, so it can be undone by removing the prefix again.
/// </summary>
public sealed class MediaAddressRewriter
{
    private readonly ApplicationDbContext _db;
    private readonly ICacheService _cache;
    private readonly ILogger<MediaAddressRewriter> _logger;

    public MediaAddressRewriter(ApplicationDbContext db, ICacheService cache, ILogger<MediaAddressRewriter> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    /// <param name="pathsInBucket">Site paths whose picture is in the bucket, e.g. "/catalog/x.webp".</param>
    /// <param name="publicBaseUrl">The bucket's public address, e.g. https://images.dealgosa.com.</param>
    /// <returns>How many addresses were changed.</returns>
    public async Task<int> RewriteAsync(
        IReadOnlyCollection<string> pathsInBucket, string publicBaseUrl, CancellationToken ct = default)
    {
        if (pathsInBucket.Count == 0) return 0;

        var paths = pathsInBucket.ToList();
        var prefix = publicBaseUrl.Trim().TrimEnd('/');

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var changed = 0;
        changed += await RewriteColumn(_db.Auctions, a => a.CoverImageUrl, paths, prefix, ct);
        changed += await RewriteColumn(_db.Auctions, a => a.MainImageSnapshot, paths, prefix, ct);
        changed += await RewriteColumn(_db.AuctionPrizes, p => p.ProductImageSnapshot, paths, prefix, ct);
        changed += await RewriteColumn(_db.Products, p => p.PrimaryImageUrl, paths, prefix, ct);
        changed += await RewriteColumn(_db.AppAds, a => a.ImageUrl, paths, prefix, ct);
        changed += await RewriteColumn(_db.StoreCategories, c => c.ImageUrl, paths, prefix, ct);
        changed += await RewriteColumn(_db.StoreProductImages, i => i.Url, paths, prefix, ct);
        changed += await RewriteColumn(_db.StoreProductImages, i => i.ThumbnailUrl, paths, prefix, ct);
        changed += await RewriteColumn(_db.StoreOrderItems, i => i.ProductImageSnapshot, paths, prefix, ct);
        changed += await RewriteColumn(_db.SiteContent, s => s.HeroImageUrl, paths, prefix, ct);
        changed += await RewriteColumn(_db.SiteCards, c => c.ImageUrl, paths, prefix, ct);
        changed += await RewriteColumn(_db.SiteTestimonials, t => t.AvatarUrl, paths, prefix, ct);

        // A prize product's gallery is a JSON list, which SQL cannot match item by item here.
        // The table holds prize products only, so reading it whole is cheap.
        var wanted = paths.ToHashSet(StringComparer.Ordinal);
        foreach (var product in await _db.Products.ToListAsync(ct))
        {
            if (!product.ImageUrls.Any(wanted.Contains)) continue;

            product.ImageUrls = product.ImageUrls
                .Select(u => wanted.Contains(u) ? prefix + u : u)
                .ToList();
            changed++;
        }
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        // Store listings are cached with the old addresses in them.
        await _cache.RemoveByPrefixAsync("store:", ct);

        _logger.LogInformation("Pointed {Count} picture addresses at {Base}.", changed, prefix);
        return changed;
    }

    /// <summary>UPDATE … SET column = prefix || column WHERE column = ANY(paths).</summary>
    private static Task<int> RewriteColumn<T>(
        IQueryable<T> set, Expression<Func<T, string?>> column, List<string> paths, string prefix,
        CancellationToken ct) where T : class
    {
        var row = column.Parameters[0];
        var contains = Expression.Call(
            Expression.Constant(paths),
            typeof(List<string>).GetMethod(nameof(List<string>.Contains))!,
            column.Body);
        var filter = Expression.Lambda<Func<T, bool>>(contains, row);

        var concat = Expression.Call(
            typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!,
            Expression.Constant(prefix),
            column.Body);
        var value = Expression.Lambda<Func<T, string?>>(concat, row);

        return set.Where(filter).ExecuteUpdateAsync(s => s.SetProperty(column, value), ct);
    }
}
