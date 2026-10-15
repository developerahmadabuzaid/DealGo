using AuctionPlatform.Domain.Common;

namespace AuctionPlatform.Domain.Entities;

/// <summary>
/// Everything the public landing page says, in one editable row.
///
/// The page is marketing, not product: its wording changes far more often than its code, and it
/// changes for reasons nobody should have to open an editor for. So none of it is written into
/// the HTML — the page asks the API what to say and an administrator writes the answer.
///
/// One row, like <see cref="StoreSettings"/>. There is a single public site.
/// </summary>
public class SiteContent : AuditableEntity
{
    // ------------------------------------------------------------------ hero

    /// <summary>The small pill above the headline. Empty hides it.</summary>
    public string? HeroBadge { get; set; }

    public string HeroTitle { get; set; } = string.Empty;
    public string? HeroSubtitle { get; set; }

    public string? HeroCtaLabel { get; set; }

    /// <summary>Where the headline button goes. Falls back to the download section.</summary>
    public string? HeroCtaUrl { get; set; }

    /// <summary>A picture beside the headline, usually a phone holding the app.</summary>
    public string? HeroImageUrl { get; set; }

    // ------------------------------------------------------------------ the app

    public string? AppStoreUrl { get; set; }
    public string? GooglePlayUrl { get; set; }

    // ------------------------------------------------------------------ about

    public bool AboutEnabled { get; set; } = true;
    public string? AboutEyebrow { get; set; }
    public string? AboutTitle { get; set; }
    public string? FeaturesTitle { get; set; }
    public string? FeaturesBody { get; set; }
    public string? VisionTitle { get; set; }
    public string? VisionBody { get; set; }

    // ------------------------------------------------------------------ figures

    public bool StatsEnabled { get; set; } = true;

    /*
     * The figures are counted from the database, not typed in, so the page cannot claim more
     * users than the platform has. These overrides exist for the one honest reason to differ:
     * publishing a rounded or lagging figure on purpose. Left empty, the real count is shown.
     */
    public int? StatUsersOverride { get; set; }
    public int? StatAuctionsOverride { get; set; }
    public int? StatProductsOverride { get; set; }

    public string? StatUsersLabel { get; set; }
    public string? StatAuctionsLabel { get; set; }
    public string? StatProductsLabel { get; set; }

    // ------------------------------------------------------------------ sections of cards

    public bool StepsEnabled { get; set; } = true;
    public string? StepsEyebrow { get; set; }
    public string? StepsTitle { get; set; }
    public string? StepsSubtitle { get; set; }

    public bool ParticipateEnabled { get; set; } = true;
    public string? ParticipateEyebrow { get; set; }
    public string? ParticipateTitle { get; set; }
    public string? ParticipateSubtitle { get; set; }

    public bool ShowcaseEnabled { get; set; } = true;
    public string? ShowcaseEyebrow { get; set; }
    public string? ShowcaseTitle { get; set; }
    public string? ShowcaseSubtitle { get; set; }

    public bool TestimonialsEnabled { get; set; } = true;
    public string? TestimonialsEyebrow { get; set; }
    public string? TestimonialsTitle { get; set; }
    public string? TestimonialsSubtitle { get; set; }

    public bool FaqEnabled { get; set; } = true;
    public string? FaqEyebrow { get; set; }
    public string? FaqTitle { get; set; }
    public string? FaqSubtitle { get; set; }

    // ------------------------------------------------------------------ closing band

    public bool DownloadEnabled { get; set; } = true;
    public string? DownloadTitle { get; set; }
    public string? DownloadSubtitle { get; set; }

    // ------------------------------------------------------------------ footer

    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? FooterNote { get; set; }

    public string? PrivacyUrl { get; set; }
    public string? TermsUrl { get; set; }

    public string? TwitterUrl { get; set; }
    public string? InstagramUrl { get; set; }
    public string? TiktokUrl { get; set; }
    public string? SnapchatUrl { get; set; }
    public string? WhatsappUrl { get; set; }

    // ------------------------------------------------------------------ search engines

    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
}

/// <summary>Which row of cards a <see cref="SiteCard"/> belongs to.</summary>
public enum SiteCardKind
{
    /// <summary>"How it works" — the numbered walk through a first auction.</summary>
    Step = 1,

    /// <summary>"How to take part" — the shorter, icon-led summary.</summary>
    Participate = 2,

    /// <summary>
    /// The picture row: an image with its own caption underneath, laid out across the page.
    ///
    /// Separate from the two card rows above because it is a different thing to look at — those
    /// explain a process in words with an icon to mark each step, this one shows the product.
    /// </summary>
    Showcase = 3
}

/// <summary>
/// One card in either of the two explanatory rows.
///
/// A list rather than a fixed set of columns: the number of steps is a content decision, and
/// wiring three of them into the schema would mean a migration to add a fourth.
/// </summary>
public class SiteCard : AuditableEntity
{
    public SiteCardKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }

    /// <summary>Names an entry in the console's own icon set, so the two never drift apart.</summary>
    public string? Icon { get; set; }

    /// <summary>
    /// A picture for this card, uploaded from the administrator's machine.
    ///
    /// When present it replaces the icon: a card cannot show both without one of them looking
    /// like a mistake. A showcase card is the picture, so it is the only row where this is the
    /// point rather than an option.
    /// </summary>
    public string? ImageUrl { get; set; }

    public int DisplayOrder { get; set; }
    public bool IsPublished { get; set; } = true;
}

/// <summary>
/// One published review.
///
/// Nothing seeds this table. A testimonial names a real person and says they said something, so
/// it can only ever be entered by someone who knows they did; sample rows would ship the site
/// with invented praise attributed to invented customers.
/// </summary>
public class SiteTestimonial : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>Out of five. Zero hides the stars rather than showing an empty row of them.</summary>
    public int Rating { get; set; }

    public string? AvatarUrl { get; set; }

    /// <summary>Shown under the name, e.g. the city they wrote from.</summary>
    public string? Role { get; set; }

    public int DisplayOrder { get; set; }
    public bool IsPublished { get; set; } = true;
}

/// <summary>One question and its answer, for the page's accordion.</summary>
public class SiteFaq : AuditableEntity
{
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;

    public int DisplayOrder { get; set; }
    public bool IsPublished { get; set; } = true;
}
