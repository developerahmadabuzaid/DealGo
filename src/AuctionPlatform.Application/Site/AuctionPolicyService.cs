using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Site;

/// <summary>
/// The auction policies as the app shows them before joining.
/// </summary>
/// <param name="Required">True when policies are published, so a join has to accept them.</param>
public sealed record AuctionPolicyDto(string Title, string Body, int Version, DateTime? UpdatedAt, bool Required);

public sealed record UpdateAuctionPolicyRequest(string? Title, string? Body);

/// <summary>
/// The policies a person accepts every time they join an auction, written in the dashboard.
///
/// They live on the single <see cref="SiteContent"/> row, next to the support number, and are read
/// and written on their own. Each change of wording raises the version, and a join must name the
/// version it accepted: someone who read an older text is sent back to read the new one.
/// </summary>
public interface IAuctionPolicyService
{
    Task<AuctionPolicyDto> GetAsync(CancellationToken ct = default);
    Task<AuctionPolicyDto> UpdateAsync(UpdateAuctionPolicyRequest request, CancellationToken ct = default);

    /// <summary>
    /// Checks a join's acceptance against the published policies. Returns the version accepted, or
    /// null when no policies are published. Throws when they are published and not accepted.
    /// </summary>
    Task<int?> RequireAcceptedAsync(int? acceptedVersion, CancellationToken ct = default);
}

public sealed class AuctionPolicyService : IAuctionPolicyService
{
    public const string DefaultTitle = "سياسات المزاد";
    public const int MaxBodyLength = 20000;

    private readonly IApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ISiteContentService _site;

    public AuctionPolicyService(
        IApplicationDbContext db, IAuditService audit, IDateTimeProvider clock, ISiteContentService site)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
        _site = site;
    }

    public async Task<AuctionPolicyDto> GetAsync(CancellationToken ct = default)
    {
        var row = await _db.SiteContent.AsNoTracking()
            .Select(s => new { s.AuctionPolicyTitle, s.AuctionPolicyBody, s.AuctionPolicyVersion, s.AuctionPolicyUpdatedAt })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? new AuctionPolicyDto(DefaultTitle, string.Empty, 0, null, false)
            : Map(row.AuctionPolicyTitle, row.AuctionPolicyBody, row.AuctionPolicyVersion, row.AuctionPolicyUpdatedAt);
    }

    public async Task<AuctionPolicyDto> UpdateAsync(UpdateAuctionPolicyRequest request, CancellationToken ct = default)
    {
        var title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
        var body = string.IsNullOrWhiteSpace(request.Body) ? null : request.Body.Replace("\r\n", "\n").Trim();

        if (title is { Length: > 200 })
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The title is longer than 200 characters.");
        }
        if (body is { Length: > MaxBodyLength })
        {
            throw new DomainException(ErrorCodes.ValidationFailed, $"The policies are longer than {MaxBodyLength} characters.");
        }

        var row = await _db.SiteContent.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            // As for the support number: reading the page creates the row with its defaults.
            await _site.GetForAdminAsync(ct);
            row = await _db.SiteContent.FirstAsync(ct);
        }

        var before = Map(row.AuctionPolicyTitle, row.AuctionPolicyBody, row.AuctionPolicyVersion, row.AuctionPolicyUpdatedAt);

        // Saving the same words again changes nothing, so nobody is asked to accept them twice.
        if (title == row.AuctionPolicyTitle && body == row.AuctionPolicyBody) return before;

        row.AuctionPolicyTitle = title;
        row.AuctionPolicyBody = body;
        row.AuctionPolicyVersion++;
        row.AuctionPolicyUpdatedAt = _clock.UtcNow;
        row.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);

        var after = Map(row.AuctionPolicyTitle, row.AuctionPolicyBody, row.AuctionPolicyVersion, row.AuctionPolicyUpdatedAt);
        await _audit.LogAsync(AuditActionType.AuctionPolicyChanged, nameof(SiteContent), row.Id,
            new { before.Version, before.Title, BodyLength = before.Body.Length },
            new { after.Version, after.Title, BodyLength = after.Body.Length }, ct: ct);

        return after;
    }

    public async Task<int?> RequireAcceptedAsync(int? acceptedVersion, CancellationToken ct = default)
    {
        var policy = await GetAsync(ct);
        if (!policy.Required) return null;

        if (acceptedVersion is null)
        {
            throw new DomainException(ErrorCodes.PoliciesNotAccepted,
                "يجب الموافقة على سياسات المزاد قبل الاشتراك. حدّث التطبيق إن لم تظهر لك.");
        }
        if (acceptedVersion != policy.Version)
        {
            throw new DomainException(ErrorCodes.PoliciesChanged,
                "تم تحديث سياسات المزاد. اقرأها ووافق عليها مرة أخرى.");
        }

        return policy.Version;
    }

    private static AuctionPolicyDto Map(string? title, string? body, int version, DateTime? updatedAt) =>
        new(string.IsNullOrWhiteSpace(title) ? DefaultTitle : title,
            body ?? string.Empty,
            version,
            updatedAt,
            !string.IsNullOrWhiteSpace(body));
}
