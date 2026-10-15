using AuctionPlatform.Domain.Entities;

namespace AuctionPlatform.Application.Site;

/// <summary>
/// Everything the landing page needs, in one response.
///
/// One request rather than six: the page is a single screen of marketing and a visitor should not
/// watch it assemble itself section by section.
/// </summary>
public sealed record SiteContentDto(
    SiteSettingsDto Settings,
    SiteStatsDto Stats,
    IReadOnlyList<SiteCardDto> Steps,
    IReadOnlyList<SiteCardDto> Participate,
    IReadOnlyList<SiteCardDto> Showcase,
    IReadOnlyList<SiteTestimonialDto> Testimonials,
    IReadOnlyList<SiteFaqDto> Faqs);

/// <summary>The editable wording. Mirrors <see cref="SiteContent"/> field for field.</summary>
public sealed record SiteSettingsDto
{
    public string? HeroBadge { get; init; }
    public string HeroTitle { get; init; } = string.Empty;
    public string? HeroSubtitle { get; init; }
    public string? HeroCtaLabel { get; init; }
    public string? HeroCtaUrl { get; init; }
    public string? HeroImageUrl { get; init; }

    public string? AppStoreUrl { get; init; }
    public string? GooglePlayUrl { get; init; }

    public bool AboutEnabled { get; init; }
    public string? AboutEyebrow { get; init; }
    public string? AboutTitle { get; init; }
    public string? FeaturesTitle { get; init; }
    public string? FeaturesBody { get; init; }
    public string? VisionTitle { get; init; }
    public string? VisionBody { get; init; }

    public bool StatsEnabled { get; init; }
    public int? StatUsersOverride { get; init; }
    public int? StatAuctionsOverride { get; init; }
    public int? StatProductsOverride { get; init; }
    public string? StatUsersLabel { get; init; }
    public string? StatAuctionsLabel { get; init; }
    public string? StatProductsLabel { get; init; }

    public bool StepsEnabled { get; init; }
    public string? StepsEyebrow { get; init; }
    public string? StepsTitle { get; init; }
    public string? StepsSubtitle { get; init; }

    public bool ParticipateEnabled { get; init; }
    public string? ParticipateEyebrow { get; init; }
    public string? ParticipateTitle { get; init; }
    public string? ParticipateSubtitle { get; init; }

    public bool ShowcaseEnabled { get; init; }
    public string? ShowcaseEyebrow { get; init; }
    public string? ShowcaseTitle { get; init; }
    public string? ShowcaseSubtitle { get; init; }

    public bool TestimonialsEnabled { get; init; }
    public string? TestimonialsEyebrow { get; init; }
    public string? TestimonialsTitle { get; init; }
    public string? TestimonialsSubtitle { get; init; }

    public bool FaqEnabled { get; init; }
    public string? FaqEyebrow { get; init; }
    public string? FaqTitle { get; init; }
    public string? FaqSubtitle { get; init; }

    public bool DownloadEnabled { get; init; }
    public string? DownloadTitle { get; init; }
    public string? DownloadSubtitle { get; init; }

    public string? ContactEmail { get; init; }
    public string? ContactPhone { get; init; }
    public string? FooterNote { get; init; }
    public string? PrivacyUrl { get; init; }
    public string? TermsUrl { get; init; }
    public string? TwitterUrl { get; init; }
    public string? InstagramUrl { get; init; }
    public string? TiktokUrl { get; init; }
    public string? SnapchatUrl { get; init; }
    public string? WhatsappUrl { get; init; }

    public string? MetaTitle { get; init; }
    public string? MetaDescription { get; init; }
}

/// <summary>
/// The figures in the band under the hero.
///
/// Counted from the database. An administrator may publish a different number, and when they do
/// <c>IsOverridden</c> says so, so nobody has to guess later why the page and the dashboard
/// disagree.
/// </summary>
public sealed record SiteStatsDto(
    int Users,
    int Auctions,
    int Products,
    bool UsersOverridden,
    bool AuctionsOverridden,
    bool ProductsOverridden);

public sealed record SiteCardDto(
    Guid Id,
    string Kind,
    string Title,
    string? Body,
    string? Icon,
    string? ImageUrl,
    int DisplayOrder,
    bool IsPublished);

public sealed record SiteTestimonialDto(
    Guid Id,
    string Name,
    string Body,
    int Rating,
    string? Role,
    string? AvatarUrl,
    int DisplayOrder,
    bool IsPublished);

public sealed record SiteFaqDto(
    Guid Id,
    string Question,
    string Answer,
    int DisplayOrder,
    bool IsPublished);

// ---------------------------------------------------------------- write requests

public sealed record SiteCardRequest(
    string Kind,
    string Title,
    string? Body,
    string? Icon,
    string? ImageUrl,
    int DisplayOrder,
    bool IsPublished);

public sealed record SiteTestimonialRequest(
    string Name,
    string Body,
    int Rating,
    string? Role,
    string? AvatarUrl,
    int DisplayOrder,
    bool IsPublished);

public sealed record SiteFaqRequest(
    string Question,
    string Answer,
    int DisplayOrder,
    bool IsPublished);
