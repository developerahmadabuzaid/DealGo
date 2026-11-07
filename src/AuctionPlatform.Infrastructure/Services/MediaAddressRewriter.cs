using System.Linq.Expressions;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// Changes the database's picture addresses, in every column that holds one.
///
/// Two uses. Pointing pictures at R2 once they are there (<see cref="RewriteAsync"/>): every picture
/// column holds either an address with its own host, which is left alone, or a path on this site
/// such as "/uploads/x.jpg" or "/catalog/x.webp". A path is rewritten to "&lt;public base&gt;/uploads/x.jpg"
/// only when that exact object is known to be in the bucket, so nothing is ever pointed at a picture
/// that is not there. And swapping a picture for a smaller copy (<see cref="ReplaceAsync"/>): each
/// exact old address becomes its new one. Both go through the same list of columns
/// (<see cref="EachColumnAsync"/>), so a column added there is covered by both.
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
        var wanted = paths.ToHashSet(StringComparer.Ordinal);

        var changed = await InTransactionAsync(
            new Prefix(paths, prefix, ct),
            url => wanted.Contains(url) ? prefix + url : url,
            ct);

        _logger.LogInformation("Pointed {Count} picture addresses at {Base}.", changed, prefix);
        return changed;
    }

    /// <summary>Each exact old address becomes its new one, wherever a picture address is kept.</summary>
    /// <returns>How many addresses were changed.</returns>
    public async Task<int> ReplaceAsync(IReadOnlyDictionary<string, string> newAddressFor, CancellationToken ct = default)
    {
        if (newAddressFor.Count == 0) return 0;

        var changed = 0;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        foreach (var (from, to) in newAddressFor)
        {
            changed += await EachColumnAsync(new Replace(from, to, ct));
        }

        changed += await EditGalleriesAsync(url => newAddressFor.TryGetValue(url, out var to) ? to : url, ct);
        await tx.CommitAsync(ct);
        await _cache.RemoveByPrefixAsync("store:", ct);

        _logger.LogInformation("Replaced {Count} picture addresses with smaller copies.", changed);
        return changed;
    }

    /// <summary>Every distinct picture address the database refers to.</summary>
    public async Task<IReadOnlyCollection<string>> AddressesInUseAsync(CancellationToken ct = default)
    {
        var collect = new Collect(ct);
        await EachColumnAsync(collect);

        foreach (var product in await _db.Products.AsNoTracking().ToListAsync(ct))
        {
            foreach (var url in product.ImageUrls.Where(u => !string.IsNullOrWhiteSpace(u))) collect.Found.Add(url);
        }

        return collect.Found;
    }

    private async Task<int> InTransactionAsync(ColumnEdit edit, Func<string, string> galleryEdit, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var changed = await EachColumnAsync(edit);
        changed += await EditGalleriesAsync(galleryEdit, ct);
        await tx.CommitAsync(ct);

        // Store listings are cached with the old addresses in them.
        await _cache.RemoveByPrefixAsync("store:", ct);
        return changed;
    }

    /// <summary>Every column that holds one picture address. Keep complete: both edits rely on it.</summary>
    private async Task<int> EachColumnAsync(ColumnEdit edit)
    {
        var changed = 0;
        changed += await edit.ApplyAsync(_db.Auctions, a => a.CoverImageUrl);
        changed += await edit.ApplyAsync(_db.Auctions, a => a.MainImageSnapshot);
        changed += await edit.ApplyAsync(_db.AuctionPrizes, p => p.ProductImageSnapshot);
        changed += await edit.ApplyAsync(_db.Products, p => p.PrimaryImageUrl);
        changed += await edit.ApplyAsync(_db.AppAds, a => a.ImageUrl);
        changed += await edit.ApplyAsync(_db.StoreCategories, c => c.ImageUrl);
        changed += await edit.ApplyAsync(_db.StoreProductImages, i => i.Url);
        changed += await edit.ApplyAsync(_db.StoreProductImages, i => i.ThumbnailUrl);
        changed += await edit.ApplyAsync(_db.StoreOrderItems, i => i.ProductImageSnapshot);
        changed += await edit.ApplyAsync(_db.SiteContent, s => s.HeroImageUrl);
        changed += await edit.ApplyAsync(_db.SiteCards, c => c.ImageUrl);
        changed += await edit.ApplyAsync(_db.SiteTestimonials, t => t.AvatarUrl);
        return changed;
    }

    /// <summary>
    /// A prize product's gallery is a JSON list, which SQL cannot match item by item here. The
    /// table holds prize products only, so reading it whole is cheap.
    /// </summary>
    private async Task<int> EditGalleriesAsync(Func<string, string> edit, CancellationToken ct)
    {
        var changed = 0;
        foreach (var product in await _db.Products.ToListAsync(ct))
        {
            var edited = product.ImageUrls.Select(edit).ToList();
            if (edited.SequenceEqual(product.ImageUrls, StringComparer.Ordinal)) continue;

            product.ImageUrls = edited;
            changed++;
        }

        await _db.SaveChangesAsync(ct);
        return changed;
    }

    // ---------------------------------------------------------------- what is done to each column

    private abstract class ColumnEdit
    {
        public abstract Task<int> ApplyAsync<T>(IQueryable<T> set, Expression<Func<T, string?>> column) where T : class;
    }

    /// <summary>UPDATE … SET column = prefix || column WHERE column = ANY(paths).</summary>
    private sealed class Prefix(List<string> paths, string prefix, CancellationToken ct) : ColumnEdit
    {
        public override Task<int> ApplyAsync<T>(IQueryable<T> set, Expression<Func<T, string?>> column)
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

    /// <summary>UPDATE … SET column = 'to' WHERE column = 'from'.</summary>
    private sealed class Replace(string from, string to, CancellationToken ct) : ColumnEdit
    {
        public override Task<int> ApplyAsync<T>(IQueryable<T> set, Expression<Func<T, string?>> column)
        {
            var row = column.Parameters[0];
            var filter = Expression.Lambda<Func<T, bool>>(
                Expression.Equal(column.Body, Expression.Constant(from, typeof(string))), row);
            var value = Expression.Lambda<Func<T, string?>>(Expression.Constant(to, typeof(string)), row);

            return set.Where(filter).ExecuteUpdateAsync(s => s.SetProperty(column, value), ct);
        }
    }

    /// <summary>Reads the distinct addresses instead of changing them.</summary>
    private sealed class Collect(CancellationToken ct) : ColumnEdit
    {
        public HashSet<string> Found { get; } = new(StringComparer.Ordinal);

        public override async Task<int> ApplyAsync<T>(IQueryable<T> set, Expression<Func<T, string?>> column)
        {
            foreach (var url in await set.AsNoTracking().Select(column).Where(u => u != null && u != "").Distinct().ToListAsync(ct))
            {
                Found.Add(url!);
            }

            return 0;
        }
    }
}
