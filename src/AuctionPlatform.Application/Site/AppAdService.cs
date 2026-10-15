using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Site;

/// <summary>One advertisement as the app and the console read it.</summary>
/// <param name="Route">
/// Where a tap goes, worked out here rather than in the app: the app should not have to know that
/// a department is reached through the store's product list, and an old app should not break when
/// that changes.
/// </param>
public sealed record AppAdDto(
    Guid Id,
    string Placement,
    string ImageUrl,
    string? Title,
    string? Note,
    string TargetKind,
    Guid? TargetId,
    string? TargetUrl,
    string? Route,
    int DisplayOrder,
    bool IsPublished,
    DateTime? StartsAt,
    DateTime? EndsAt);

public sealed record AppAdRequest(
    string Placement,
    string ImageUrl,
    string? Title = null,
    string? Note = null,
    string? TargetKind = null,
    Guid? TargetId = null,
    string? TargetUrl = null,
    int DisplayOrder = 0,
    bool IsPublished = true,
    DateTime? StartsAt = null,
    DateTime? EndsAt = null);

public interface IAppAdService
{
    /// <summary>What the app draws: published, in season, in display order.</summary>
    Task<IReadOnlyList<AppAdDto>> GetLiveAsync(CancellationToken ct = default);

    /// <summary>Every advertisement, drafts and finished campaigns included, for the console.</summary>
    Task<IReadOnlyList<AppAdDto>> GetAllAsync(CancellationToken ct = default);

    Task<AppAdDto> SaveAsync(Guid? id, AppAdRequest request, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// The advertisements on the app's front page.
///
/// Nothing here decides what an advertisement means; it stores a picture, where it goes and when
/// it runs. The one piece of judgement is <see cref="RouteFor"/>, which turns "this ad points at
/// that auction" into the address the app opens — kept on the server so the two never disagree.
/// </summary>
public sealed class AppAdService : IAppAdService
{
    private readonly IApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;

    public AppAdService(IApplicationDbContext db, IAuditService audit, IDateTimeProvider clock)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
    }

    public async Task<IReadOnlyList<AppAdDto>> GetLiveAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        var ads = await _db.AppAds.AsNoTracking()
            .Where(a => a.IsPublished
                && (a.StartsAt == null || a.StartsAt <= now)
                && (a.EndsAt == null || a.EndsAt >= now))
            .OrderBy(a => a.Placement).ThenBy(a => a.DisplayOrder).ThenBy(a => a.CreatedAt)
            .ToListAsync(ct);

        return ads.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<AppAdDto>> GetAllAsync(CancellationToken ct = default)
    {
        var ads = await _db.AppAds.AsNoTracking()
            .OrderBy(a => a.Placement).ThenBy(a => a.DisplayOrder).ThenBy(a => a.CreatedAt)
            .ToListAsync(ct);

        return ads.Select(Map).ToList();
    }

    public async Task<AppAdDto> SaveAsync(
        Guid? id, AppAdRequest request, CancellationToken ct = default)
    {
        if (!Enum.TryParse<AppAdPlacement>(request.Placement, ignoreCase: true, out var placement))
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed,
                "An advertisement belongs to 'HomeStrip', 'HomeSlideshow' or 'HomeUnderCategories'.");
        }

        var target = AppAdTargetKind.None;
        if (!string.IsNullOrWhiteSpace(request.TargetKind)
            && !Enum.TryParse(request.TargetKind, ignoreCase: true, out target))
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed,
                "An advertisement opens 'None', 'Auction', 'Product', 'Category', 'Url' "
                + "or 'WhatsApp'.");
        }

        // The picture is the advertisement. Without one the app would draw an empty box.
        if (string.IsNullOrWhiteSpace(request.ImageUrl))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "An advertisement needs a picture.");
        }

        // A tap that goes nowhere is a choice; a tap aimed at nothing in particular is a mistake.
        if (target is AppAdTargetKind.Auction or AppAdTargetKind.Product or AppAdTargetKind.Category
            && request.TargetId is null)
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed, "Choose what this advertisement opens.");
        }

        if (target == AppAdTargetKind.Url && string.IsNullOrWhiteSpace(request.TargetUrl))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A link advertisement needs an address.");
        }

        /*
         * A WhatsApp advertisement is only as good as its number.
         *
         * It is checked and put into canonical form here rather than when it is read, so a number
         * that cannot be dialled is refused while an administrator is still looking at the form —
         * not discovered later by a customer tapping an advertisement and landing nowhere.
         */
        string? whatsapp = null;
        if (target == AppAdTargetKind.WhatsApp)
        {
            if (string.IsNullOrWhiteSpace(request.TargetUrl))
            {
                throw new DomainException(
                    ErrorCodes.ValidationFailed, "A WhatsApp advertisement needs a mobile number.");
            }

            whatsapp = SaudiPhone.Normalise(request.TargetUrl);
        }

        if (request.StartsAt is { } from && request.EndsAt is { } to && to <= from)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The end must come after the start.");
        }

        var ad = id is null
            ? Track(new AppAd())
            : await _db.AppAds.FirstOrDefaultAsync(a => a.Id == id, ct)
              ?? throw new DomainException(ErrorCodes.NotFound, "That advertisement no longer exists.");

        ad.Placement = placement;
        ad.ImageUrl = request.ImageUrl.Trim();
        ad.Title = Trim(request.Title);
        ad.Note = Trim(request.Note);
        ad.TargetKind = target;
        ad.TargetId = target is AppAdTargetKind.Auction or AppAdTargetKind.Product
            or AppAdTargetKind.Category
            ? request.TargetId
            : null;
        ad.TargetUrl = target switch
        {
            AppAdTargetKind.Url => Trim(request.TargetUrl),
            AppAdTargetKind.WhatsApp => whatsapp,
            _ => null
        };
        ad.DisplayOrder = request.DisplayOrder;
        ad.IsPublished = request.IsPublished;
        ad.StartsAt = Utc(request.StartsAt);
        ad.EndsAt = Utc(request.EndsAt);
        ad.UpdatedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(ct);
        await LogAsync(ad.Id, id is null ? "Created" : "Updated", ct);

        return Map(ad);

        AppAd Track(AppAd a)
        {
            _db.AppAds.Add(a);
            return a;
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var ad = await _db.AppAds.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (ad is null) return;

        _db.AppAds.Remove(ad);
        await _db.SaveChangesAsync(ct);
        await LogAsync(id, "Deleted", ct);
    }

    private Task LogAsync(Guid id, string action, CancellationToken ct) =>
        _audit.LogAsync(AuditActionType.AppAdChanged, nameof(AppAd), id, null, new { Action = action }, ct: ct);

    private static AppAdDto Map(AppAd a) => new(
        a.Id,
        a.Placement.ToString(),
        a.ImageUrl,
        a.Title,
        a.Note,
        a.TargetKind.ToString(),
        a.TargetId,
        a.TargetUrl,
        RouteFor(a),
        a.DisplayOrder,
        a.IsPublished,
        a.StartsAt,
        a.EndsAt);

    /// <summary>The address the app opens, or null when the advertisement is only a picture.</summary>
    private static string? RouteFor(AppAd a) => a.TargetKind switch
    {
        AppAdTargetKind.Auction when a.TargetId is { } id => $"/auctions/{id}",
        AppAdTargetKind.Product when a.TargetId is { } id => $"/store/products/{id}",
        AppAdTargetKind.Category when a.TargetId is { } id => $"/store/products?categoryId={id}",
        AppAdTargetKind.Url => a.TargetUrl,

        // wa.me wants the number as digits alone, so the leading + is dropped. Opening this
        // address on a phone opens the conversation in WhatsApp itself.
        AppAdTargetKind.WhatsApp when a.TargetUrl is { Length: > 1 } phone =>
            $"https://wa.me/{phone.TrimStart('+')}",

        _ => null
    };

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A campaign's dates are stored in UTC. A date that arrived without a zone is read as UTC
    /// rather than as the server's own time, so the same request means the same instant wherever
    /// the server happens to be running.
    /// </summary>
    private static DateTime? Utc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } => value,
        { Kind: DateTimeKind.Local } => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
    };
}
