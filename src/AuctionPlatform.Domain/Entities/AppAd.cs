using AuctionPlatform.Domain.Common;

namespace AuctionPlatform.Domain.Entities;

/// <summary>Where on the app's front page an advertisement is drawn.</summary>
public enum AppAdPlacement
{
    /// <summary>The thin strip under the search box. The picture alone, changing by itself.</summary>
    HomeStrip = 0,

    /// <summary>The large moving pictures under that strip, with a name and a note written on them.</summary>
    HomeSlideshow = 1,

    /// <summary>The one still picture under the departments row.</summary>
    HomeUnderCategories = 2
}

/// <summary>What tapping an advertisement opens.</summary>
public enum AppAdTargetKind
{
    /// <summary>Nothing. The picture is the whole message.</summary>
    None = 0,
    Auction = 1,
    Product = 2,
    Category = 3,

    /// <summary>An address outside the app, opened in the phone's browser.</summary>
    Url = 4,

    /// <summary>
    /// A WhatsApp conversation with a number the advertiser gave. Appended rather than inserted:
    /// these values are stored as integers, so renumbering the ones above would silently change
    /// what every existing advertisement points at.
    /// </summary>
    WhatsApp = 5
}

/// <summary>
/// One advertisement on the app's front page.
///
/// Before this existed the app advertised whatever happened to be in the data — an auction's cover
/// picture, a product's photograph. That is a reasonable default and it is still what happens when
/// this table is empty, but it is not advertising: a campaign has its own artwork, its own order,
/// its own start and end, and none of that belongs to a product row.
/// </summary>
public class AppAd : AuditableEntity
{
    public AppAdPlacement Placement { get; set; }

    /// <summary>The artwork. An advertisement without one is nothing, so it is required.</summary>
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>Written over the picture in the large slideshow. The other two places ignore it.</summary>
    public string? Title { get; set; }

    /// <summary>The smaller line under the title, e.g. a price or a discount.</summary>
    public string? Note { get; set; }

    public AppAdTargetKind TargetKind { get; set; }

    /// <summary>Which auction, product or department, when the target is one of those.</summary>
    public Guid? TargetId { get; set; }

    /// <summary>
    /// The address when the target is a link, or the phone number when it is a WhatsApp chat.
    ///
    /// A number is kept in the one canonical form the rest of the platform uses, +9665XXXXXXXX, so
    /// the same number typed two ways is one number. The wa.me address itself is never stored —
    /// it is built when the advertisement is read, beside every other route.
    /// </summary>
    public string? TargetUrl { get; set; }

    public int DisplayOrder { get; set; }
    public bool IsPublished { get; set; } = true;

    /*
     * A campaign runs for a while. Both ends are optional and both are UTC: an empty start means
     * "as soon as it is published", an empty end means "until somebody stops it".
     */
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
}
