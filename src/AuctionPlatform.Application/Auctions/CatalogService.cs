using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Auctions;

public sealed record CatalogProductDto(
    Guid Id,
    string Name,
    string? Description,
    decimal Price,
    string Currency,
    string? PrimaryImageUrl,
    bool IsActive,
    DateTime CreatedAt);

public sealed record SaveCatalogProductRequest(
    string Name, string? Description, decimal Price, string? Currency, string? PrimaryImageUrl, bool? IsActive);

/// <summary>
/// The auction prize catalog. Kept separate from the shop's sellable inventory: these products are
/// awarded, not sold, and they carry no stock.
/// </summary>
public interface ICatalogService
{
    Task<PagedResult<CatalogProductDto>> ListAsync(PagedQuery query, CancellationToken ct = default);
    Task<CatalogProductDto> GetAsync(Guid id, CancellationToken ct = default);
    Task<CatalogProductDto> CreateAsync(SaveCatalogProductRequest request, CancellationToken ct = default);
    Task<CatalogProductDto> UpdateAsync(Guid id, SaveCatalogProductRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public sealed class CatalogService : ICatalogService
{
    private readonly IApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public CatalogService(IApplicationDbContext db, IDateTimeProvider clock, IAuditService audit)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
    }

    public async Task<PagedResult<CatalogProductDto>> ListAsync(PagedQuery query, CancellationToken ct = default)
    {
        var q = _db.Products.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(p => p.Name.ToLower().Contains(term));
        }

        var total = await q.LongCountAsync(ct);

        var items = await q
            .OrderByDescending(p => p.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(p => new CatalogProductDto(
                p.Id, p.Name, p.Description, p.Price, p.Currency, p.PrimaryImageUrl, p.IsActive, p.CreatedAt))
            .ToListAsync(ct);

        return PagedResult<CatalogProductDto>.Create(items, query.Page, query.PageSize, total);
    }

    public async Task<CatalogProductDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new DomainException(ErrorCodes.ProductNotFound, "Product not found.");

        return Map(product);
    }

    public async Task<CatalogProductDto> CreateAsync(SaveCatalogProductRequest request, CancellationToken ct = default)
    {
        var product = new Product
        {
            Name = request.Name,
            Description = request.Description,
            Price = Money.Round(request.Price),
            Currency = request.Currency ?? Money.DefaultCurrency,
            PrimaryImageUrl = request.PrimaryImageUrl,
            IsActive = request.IsActive ?? true,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };

        _db.Products.Add(product);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.StoreProductCreated, nameof(Product), product.Id,
            null, new { product.Name, product.Price }, "Auction prize catalog", ct);

        return Map(product);
    }

    public async Task<CatalogProductDto> UpdateAsync(
        Guid id, SaveCatalogProductRequest request, CancellationToken ct = default)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new DomainException(ErrorCodes.ProductNotFound, "Product not found.");

        var before = new { product.Name, product.Price };

        product.Name = request.Name;
        product.Description = request.Description;
        // Editing a catalog price never changes a published auction: those hold their own snapshots.
        product.Price = Money.Round(request.Price);
        if (request.Currency is not null) product.Currency = request.Currency;
        product.PrimaryImageUrl = request.PrimaryImageUrl;
        if (request.IsActive.HasValue) product.IsActive = request.IsActive.Value;
        product.UpdatedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.StoreProductUpdated, nameof(Product), id,
            before, new { product.Name, product.Price }, "Auction prize catalog", ct);

        return Map(product);
    }

    /// <summary>A product used by any auction is deactivated rather than deleted.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new DomainException(ErrorCodes.ProductNotFound, "Product not found.");

        var inUse = await _db.AuctionPrizes.AnyAsync(p => p.ProductId == id, ct) ||
                    await _db.Auctions.AnyAsync(a => a.MainProductId == id, ct);

        if (inUse)
        {
            product.IsActive = false;
            product.UpdatedAt = _clock.UtcNow;
        }
        else
        {
            _db.Products.Remove(product);
        }

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.StoreProductArchived, nameof(Product), id,
            new { product.Name }, null, inUse ? "Deactivated: referenced by auctions" : "Deleted", ct);
    }

    private static CatalogProductDto Map(Product p) =>
        new(p.Id, p.Name, p.Description, p.Price, p.Currency, p.PrimaryImageUrl, p.IsActive, p.CreatedAt);
}
