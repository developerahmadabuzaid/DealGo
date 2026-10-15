using AuctionPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuctionPlatform.Infrastructure.Persistence.Configurations;

/// <summary>
/// The public landing page's content.
///
/// Lengths are generous but bounded. Every one of these fields ends up rendered into a page, and
/// an unbounded text column is an invitation to paste a novel into a headline.
/// </summary>
public sealed class SiteContentConfiguration : IEntityTypeConfiguration<SiteContent>
{
    public void Configure(EntityTypeBuilder<SiteContent> b)
    {
        b.ToTable("SiteContent");
        b.HasKey(s => s.Id);

        b.Property(s => s.HeroBadge).HasMaxLength(200);
        b.Property(s => s.HeroTitle).HasMaxLength(300).IsRequired();
        b.Property(s => s.HeroSubtitle).HasMaxLength(1000);
        b.Property(s => s.HeroCtaLabel).HasMaxLength(100);
        b.Property(s => s.HeroCtaUrl).HasMaxLength(1024);
        b.Property(s => s.HeroImageUrl).HasMaxLength(1024);

        b.Property(s => s.AppStoreUrl).HasMaxLength(1024);
        b.Property(s => s.GooglePlayUrl).HasMaxLength(1024);

        b.Property(s => s.AboutEyebrow).HasMaxLength(120);
        b.Property(s => s.AboutTitle).HasMaxLength(300);
        b.Property(s => s.FeaturesTitle).HasMaxLength(200);
        b.Property(s => s.FeaturesBody).HasMaxLength(2000);
        b.Property(s => s.VisionTitle).HasMaxLength(200);
        b.Property(s => s.VisionBody).HasMaxLength(2000);

        b.Property(s => s.StatUsersLabel).HasMaxLength(120);
        b.Property(s => s.StatAuctionsLabel).HasMaxLength(120);
        b.Property(s => s.StatProductsLabel).HasMaxLength(120);

        b.Property(s => s.StepsEyebrow).HasMaxLength(120);
        b.Property(s => s.StepsTitle).HasMaxLength(300);
        b.Property(s => s.StepsSubtitle).HasMaxLength(1000);

        b.Property(s => s.ParticipateEyebrow).HasMaxLength(120);
        b.Property(s => s.ParticipateTitle).HasMaxLength(300);
        b.Property(s => s.ParticipateSubtitle).HasMaxLength(1000);

        b.Property(s => s.ShowcaseEyebrow).HasMaxLength(120);
        b.Property(s => s.ShowcaseTitle).HasMaxLength(300);
        b.Property(s => s.ShowcaseSubtitle).HasMaxLength(1000);

        b.Property(s => s.TestimonialsEyebrow).HasMaxLength(120);
        b.Property(s => s.TestimonialsTitle).HasMaxLength(300);
        b.Property(s => s.TestimonialsSubtitle).HasMaxLength(1000);

        b.Property(s => s.FaqEyebrow).HasMaxLength(120);
        b.Property(s => s.FaqTitle).HasMaxLength(300);
        b.Property(s => s.FaqSubtitle).HasMaxLength(1000);

        b.Property(s => s.DownloadTitle).HasMaxLength(300);
        b.Property(s => s.DownloadSubtitle).HasMaxLength(1000);

        b.Property(s => s.ContactEmail).HasMaxLength(256);
        b.Property(s => s.ContactPhone).HasMaxLength(64);
        b.Property(s => s.FooterNote).HasMaxLength(500);

        b.Property(s => s.PrivacyUrl).HasMaxLength(1024);
        b.Property(s => s.TermsUrl).HasMaxLength(1024);
        b.Property(s => s.TwitterUrl).HasMaxLength(1024);
        b.Property(s => s.InstagramUrl).HasMaxLength(1024);
        b.Property(s => s.TiktokUrl).HasMaxLength(1024);
        b.Property(s => s.SnapchatUrl).HasMaxLength(1024);
        b.Property(s => s.WhatsappUrl).HasMaxLength(1024);

        b.Property(s => s.MetaTitle).HasMaxLength(200);
        b.Property(s => s.MetaDescription).HasMaxLength(500);
    }
}

public sealed class SiteCardConfiguration : IEntityTypeConfiguration<SiteCard>
{
    public void Configure(EntityTypeBuilder<SiteCard> b)
    {
        b.ToTable("SiteCards");
        b.HasKey(c => c.Id);

        b.Property(c => c.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(c => c.Title).HasMaxLength(200).IsRequired();
        b.Property(c => c.Body).HasMaxLength(1000);
        b.Property(c => c.Icon).HasMaxLength(64);
        b.Property(c => c.ImageUrl).HasMaxLength(1024);

        // The page reads one row of cards at a time, already in order.
        b.HasIndex(c => new { c.Kind, c.DisplayOrder });
    }
}

public sealed class SiteTestimonialConfiguration : IEntityTypeConfiguration<SiteTestimonial>
{
    public void Configure(EntityTypeBuilder<SiteTestimonial> b)
    {
        b.ToTable("SiteTestimonials");
        b.HasKey(t => t.Id);

        b.Property(t => t.Name).HasMaxLength(120).IsRequired();
        b.Property(t => t.Body).HasMaxLength(1000).IsRequired();
        b.Property(t => t.Role).HasMaxLength(120);
        b.Property(t => t.AvatarUrl).HasMaxLength(1024);

        b.HasIndex(t => new { t.IsPublished, t.DisplayOrder });
    }
}

/// <summary>
/// The app's own advertisements.
///
/// Read one place at a time, published rows only, in order — so that is what the index covers.
/// </summary>
public sealed class AppAdConfiguration : IEntityTypeConfiguration<AppAd>
{
    public void Configure(EntityTypeBuilder<AppAd> b)
    {
        b.ToTable("AppAds");
        b.HasKey(a => a.Id);

        b.Property(a => a.Placement).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(a => a.TargetKind).HasConversion<string>().HasMaxLength(32).IsRequired();

        b.Property(a => a.ImageUrl).HasMaxLength(1024).IsRequired();
        b.Property(a => a.Title).HasMaxLength(200);
        b.Property(a => a.Note).HasMaxLength(200);
        b.Property(a => a.TargetUrl).HasMaxLength(1024);

        b.HasIndex(a => new { a.Placement, a.IsPublished, a.DisplayOrder });
    }
}

public sealed class SiteFaqConfiguration : IEntityTypeConfiguration<SiteFaq>
{
    public void Configure(EntityTypeBuilder<SiteFaq> b)
    {
        b.ToTable("SiteFaqs");
        b.HasKey(f => f.Id);

        b.Property(f => f.Question).HasMaxLength(300).IsRequired();
        b.Property(f => f.Answer).HasMaxLength(4000).IsRequired();

        b.HasIndex(f => new { f.IsPublished, f.DisplayOrder });
    }
}
