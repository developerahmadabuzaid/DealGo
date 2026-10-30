using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Site;

/// <summary>How people reach the support team from the app.</summary>
public sealed record SupportSettingsDto(string WhatsappNumber);

public sealed record UpdateSupportSettingsRequest(string WhatsappNumber);

/// <summary>
/// The support line: one WhatsApp number, set in the console, that the app's WhatsApp button
/// opens a chat with.
///
/// It lives on the single <see cref="SiteContent"/> row so there is one place for what the
/// platform publishes about itself, but it is read and written on its own: the app needs the
/// number without the whole landing page, and changing it should not mean re-saving every
/// headline on that page.
/// </summary>
public interface ISupportService
{
    Task<SupportSettingsDto> GetAsync(CancellationToken ct = default);
    Task<SupportSettingsDto> UpdateAsync(UpdateSupportSettingsRequest request, CancellationToken ct = default);
}

public sealed class SupportService : ISupportService
{
    private readonly IApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ISiteContentService _site;

    public SupportService(
        IApplicationDbContext db, IAuditService audit, IDateTimeProvider clock, ISiteContentService site)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
        _site = site;
    }

    /// <summary>
    /// The number to show. Before anyone has saved the landing page there is no row yet, and the
    /// default stands in rather than the button disappearing.
    /// </summary>
    public async Task<SupportSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var number = await _db.SiteContent.AsNoTracking()
            .Select(s => s.SupportWhatsapp)
            .FirstOrDefaultAsync(ct);

        return new SupportSettingsDto(
            string.IsNullOrWhiteSpace(number) ? SiteContent.DefaultSupportWhatsapp : number);
    }

    public async Task<SupportSettingsDto> UpdateAsync(
        UpdateSupportSettingsRequest request, CancellationToken ct = default)
    {
        // The support line is a Saudi mobile, written in the one form wa.me understands.
        var number = SaudiPhone.Normalise(request.WhatsappNumber);

        var row = await _db.SiteContent.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            // The row is created, with the landing page's starter wording and cards, the first
            // time the page is read. Reading it here does the same, so saving the number first
            // does not leave the page without its defaults.
            await _site.GetForAdminAsync(ct);
            row = await _db.SiteContent.FirstAsync(ct);
        }

        var before = new SupportSettingsDto(row.SupportWhatsapp);

        row.SupportWhatsapp = number;
        row.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);

        var after = new SupportSettingsDto(number);
        await _audit.LogAsync(AuditActionType.SupportSettingsChanged, nameof(SiteContent), row.Id,
            before, after, ct: ct);

        return after;
    }
}
