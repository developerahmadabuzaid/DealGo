using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Site;

public interface ISiteContentService
{
    /// <summary>What the landing page renders. Published rows only, sections in display order.</summary>
    Task<SiteContentDto> GetPublicAsync(CancellationToken ct = default);

    /// <summary>The same content with the unpublished rows included, for the console.</summary>
    Task<SiteContentDto> GetForAdminAsync(CancellationToken ct = default);

    Task<SiteSettingsDto> UpdateSettingsAsync(SiteSettingsDto request, CancellationToken ct = default);

    Task<SiteCardDto> SaveCardAsync(Guid? id, SiteCardRequest request, CancellationToken ct = default);
    Task DeleteCardAsync(Guid id, CancellationToken ct = default);

    Task<SiteTestimonialDto> SaveTestimonialAsync(
        Guid? id, SiteTestimonialRequest request, CancellationToken ct = default);
    Task DeleteTestimonialAsync(Guid id, CancellationToken ct = default);

    Task<SiteFaqDto> SaveFaqAsync(Guid? id, SiteFaqRequest request, CancellationToken ct = default);
    Task DeleteFaqAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Reads and writes the public landing page's content.
///
/// Two things worth knowing about the figures in the "stats" band. They are counted here rather
/// than typed in, so the page cannot advertise a number the platform cannot back; and an
/// administrator who overrides one is recorded as having done so, because a marketing figure that
/// silently stopped matching the dashboard is the kind of thing nobody can explain a year later.
/// </summary>
public sealed class SiteContentService : ISiteContentService
{
    private readonly IApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;

    public SiteContentService(IApplicationDbContext db, IAuditService audit, IDateTimeProvider clock)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
    }

    // ---------------------------------------------------------------- reads

    public Task<SiteContentDto> GetPublicAsync(CancellationToken ct = default) =>
        BuildAsync(publishedOnly: true, ct);

    public Task<SiteContentDto> GetForAdminAsync(CancellationToken ct = default) =>
        BuildAsync(publishedOnly: false, ct);

    private async Task<SiteContentDto> BuildAsync(bool publishedOnly, CancellationToken ct)
    {
        var settings = await GetSettingsEntityAsync(ct);

        var cards = await _db.SiteCards.AsNoTracking()
            .Where(c => !publishedOnly || c.IsPublished)
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var testimonials = await _db.SiteTestimonials.AsNoTracking()
            .Where(t => !publishedOnly || t.IsPublished)
            .OrderBy(t => t.DisplayOrder).ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);

        var faqs = await _db.SiteFaqs.AsNoTracking()
            .Where(f => !publishedOnly || f.IsPublished)
            .OrderBy(f => f.DisplayOrder).ThenBy(f => f.CreatedAt)
            .ToListAsync(ct);

        return new SiteContentDto(
            MapSettings(settings),
            await CountAsync(settings, ct),
            cards.Where(c => c.Kind == SiteCardKind.Step).Select(MapCard).ToList(),
            cards.Where(c => c.Kind == SiteCardKind.Participate).Select(MapCard).ToList(),
            cards.Where(c => c.Kind == SiteCardKind.Showcase).Select(MapCard).ToList(),
            testimonials.Select(MapTestimonial).ToList(),
            faqs.Select(MapFaq).ToList());
    }

    /// <summary>
    /// The three headline figures.
    ///
    /// "Users" counts the people who can actually sign in, so a disabled account is not paraded
    /// as a member. "Auctions" counts the ones that reached the public, never a draft. "Products"
    /// counts what the store is currently offering.
    /// </summary>
    private async Task<SiteStatsDto> CountAsync(SiteContent s, CancellationToken ct)
    {
        var users = s.StatUsersOverride
            ?? await _db.Users.AsNoTracking().CountAsync(u => u.IsActive, ct);

        var auctions = s.StatAuctionsOverride
            ?? await _db.Auctions.AsNoTracking().CountAsync(a => a.Status != AuctionStatus.Draft, ct);

        var products = s.StatProductsOverride
            ?? await _db.StoreProducts.AsNoTracking()
                .CountAsync(p => p.Status == StoreProductStatus.Active, ct);

        return new SiteStatsDto(
            users, auctions, products,
            s.StatUsersOverride.HasValue,
            s.StatAuctionsOverride.HasValue,
            s.StatProductsOverride.HasValue);
    }

    // ---------------------------------------------------------------- settings

    public async Task<SiteSettingsDto> UpdateSettingsAsync(
        SiteSettingsDto request, CancellationToken ct = default)
    {
        var s = await GetSettingsEntityAsync(ct);
        var before = MapSettings(s);

        s.HeroBadge = Trim(request.HeroBadge);
        s.HeroTitle = Trim(request.HeroTitle) ?? string.Empty;
        s.HeroSubtitle = Trim(request.HeroSubtitle);
        s.HeroCtaLabel = Trim(request.HeroCtaLabel);
        s.HeroCtaUrl = Trim(request.HeroCtaUrl);
        s.HeroImageUrl = Trim(request.HeroImageUrl);

        s.AppStoreUrl = Trim(request.AppStoreUrl);
        s.GooglePlayUrl = Trim(request.GooglePlayUrl);

        s.AboutEnabled = request.AboutEnabled;
        s.AboutEyebrow = Trim(request.AboutEyebrow);
        s.AboutTitle = Trim(request.AboutTitle);
        s.FeaturesTitle = Trim(request.FeaturesTitle);
        s.FeaturesBody = Trim(request.FeaturesBody);
        s.VisionTitle = Trim(request.VisionTitle);
        s.VisionBody = Trim(request.VisionBody);

        s.StatsEnabled = request.StatsEnabled;
        s.StatUsersOverride = NonNegative(request.StatUsersOverride);
        s.StatAuctionsOverride = NonNegative(request.StatAuctionsOverride);
        s.StatProductsOverride = NonNegative(request.StatProductsOverride);
        s.StatUsersLabel = Trim(request.StatUsersLabel);
        s.StatAuctionsLabel = Trim(request.StatAuctionsLabel);
        s.StatProductsLabel = Trim(request.StatProductsLabel);

        s.StepsEnabled = request.StepsEnabled;
        s.StepsEyebrow = Trim(request.StepsEyebrow);
        s.StepsTitle = Trim(request.StepsTitle);
        s.StepsSubtitle = Trim(request.StepsSubtitle);

        s.ParticipateEnabled = request.ParticipateEnabled;
        s.ParticipateEyebrow = Trim(request.ParticipateEyebrow);
        s.ParticipateTitle = Trim(request.ParticipateTitle);
        s.ParticipateSubtitle = Trim(request.ParticipateSubtitle);

        s.ShowcaseEnabled = request.ShowcaseEnabled;
        s.ShowcaseEyebrow = Trim(request.ShowcaseEyebrow);
        s.ShowcaseTitle = Trim(request.ShowcaseTitle);
        s.ShowcaseSubtitle = Trim(request.ShowcaseSubtitle);

        s.TestimonialsEnabled = request.TestimonialsEnabled;
        s.TestimonialsEyebrow = Trim(request.TestimonialsEyebrow);
        s.TestimonialsTitle = Trim(request.TestimonialsTitle);
        s.TestimonialsSubtitle = Trim(request.TestimonialsSubtitle);

        s.FaqEnabled = request.FaqEnabled;
        s.FaqEyebrow = Trim(request.FaqEyebrow);
        s.FaqTitle = Trim(request.FaqTitle);
        s.FaqSubtitle = Trim(request.FaqSubtitle);

        s.DownloadEnabled = request.DownloadEnabled;
        s.DownloadTitle = Trim(request.DownloadTitle);
        s.DownloadSubtitle = Trim(request.DownloadSubtitle);

        s.ContactEmail = Trim(request.ContactEmail);
        s.ContactPhone = Trim(request.ContactPhone);
        s.FooterNote = Trim(request.FooterNote);
        s.PrivacyUrl = Trim(request.PrivacyUrl);
        s.TermsUrl = Trim(request.TermsUrl);
        s.TwitterUrl = Trim(request.TwitterUrl);
        s.InstagramUrl = Trim(request.InstagramUrl);
        s.TiktokUrl = Trim(request.TiktokUrl);
        s.SnapchatUrl = Trim(request.SnapchatUrl);
        s.WhatsappUrl = Trim(request.WhatsappUrl);

        s.MetaTitle = Trim(request.MetaTitle);
        s.MetaDescription = Trim(request.MetaDescription);

        if (string.IsNullOrWhiteSpace(s.HeroTitle))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The page needs a headline.");
        }

        s.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.SiteContentChanged, nameof(SiteContent), s.Id,
            before, MapSettings(s), ct: ct);

        return MapSettings(s);
    }

    // ---------------------------------------------------------------- cards

    public async Task<SiteCardDto> SaveCardAsync(
        Guid? id, SiteCardRequest request, CancellationToken ct = default)
    {
        if (!Enum.TryParse<SiteCardKind>(request.Kind, ignoreCase: true, out var kind))
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed,
                "A card belongs to 'Step', 'Participate' or 'Showcase'.");
        }

        // A picture row with no picture is an empty box on the page, so it is refused here
        // rather than published and wondered about later.
        if (kind == SiteCardKind.Showcase && string.IsNullOrWhiteSpace(request.ImageUrl))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A picture card needs a picture.");
        }

        Require(request.Title, "A card needs a title.");

        var card = id is null
            ? Track(new SiteCard())
            : await _db.SiteCards.FirstOrDefaultAsync(c => c.Id == id, ct)
              ?? throw new DomainException(ErrorCodes.NotFound, "That card no longer exists.");

        card.Kind = kind;
        card.Title = request.Title.Trim();
        card.Body = Trim(request.Body);
        card.Icon = Trim(request.Icon);
        card.ImageUrl = Trim(request.ImageUrl);
        card.DisplayOrder = request.DisplayOrder;
        card.IsPublished = request.IsPublished;
        card.UpdatedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(ct);
        await LogAsync(nameof(SiteCard), card.Id, id is null, ct);

        return MapCard(card);

        SiteCard Track(SiteCard c)
        {
            _db.SiteCards.Add(c);
            return c;
        }
    }

    public async Task DeleteCardAsync(Guid id, CancellationToken ct = default)
    {
        var card = await _db.SiteCards.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (card is null) return;

        _db.SiteCards.Remove(card);
        await _db.SaveChangesAsync(ct);
        await LogAsync(nameof(SiteCard), id, created: false, ct, deleted: true);
    }

    // ---------------------------------------------------------------- testimonials

    public async Task<SiteTestimonialDto> SaveTestimonialAsync(
        Guid? id, SiteTestimonialRequest request, CancellationToken ct = default)
    {
        Require(request.Name, "A review needs the name of the person who wrote it.");
        Require(request.Body, "A review needs its text.");

        if (request.Rating is < 0 or > 5)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A rating runs from 0 to 5.");
        }

        var t = id is null
            ? Track(new SiteTestimonial())
            : await _db.SiteTestimonials.FirstOrDefaultAsync(x => x.Id == id, ct)
              ?? throw new DomainException(ErrorCodes.NotFound, "That review no longer exists.");

        t.Name = request.Name.Trim();
        t.Body = request.Body.Trim();
        t.Rating = request.Rating;
        t.Role = Trim(request.Role);
        t.AvatarUrl = Trim(request.AvatarUrl);
        t.DisplayOrder = request.DisplayOrder;
        t.IsPublished = request.IsPublished;
        t.UpdatedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(ct);
        await LogAsync(nameof(SiteTestimonial), t.Id, id is null, ct);

        return MapTestimonial(t);

        SiteTestimonial Track(SiteTestimonial x)
        {
            _db.SiteTestimonials.Add(x);
            return x;
        }
    }

    public async Task DeleteTestimonialAsync(Guid id, CancellationToken ct = default)
    {
        var t = await _db.SiteTestimonials.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return;

        _db.SiteTestimonials.Remove(t);
        await _db.SaveChangesAsync(ct);
        await LogAsync(nameof(SiteTestimonial), id, created: false, ct, deleted: true);
    }

    // ---------------------------------------------------------------- questions

    public async Task<SiteFaqDto> SaveFaqAsync(
        Guid? id, SiteFaqRequest request, CancellationToken ct = default)
    {
        Require(request.Question, "A question needs its text.");
        Require(request.Answer, "A question needs an answer.");

        var f = id is null
            ? Track(new SiteFaq())
            : await _db.SiteFaqs.FirstOrDefaultAsync(x => x.Id == id, ct)
              ?? throw new DomainException(ErrorCodes.NotFound, "That question no longer exists.");

        f.Question = request.Question.Trim();
        f.Answer = request.Answer.Trim();
        f.DisplayOrder = request.DisplayOrder;
        f.IsPublished = request.IsPublished;
        f.UpdatedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(ct);
        await LogAsync(nameof(SiteFaq), f.Id, id is null, ct);

        return MapFaq(f);

        SiteFaq Track(SiteFaq x)
        {
            _db.SiteFaqs.Add(x);
            return x;
        }
    }

    public async Task DeleteFaqAsync(Guid id, CancellationToken ct = default)
    {
        var f = await _db.SiteFaqs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return;

        _db.SiteFaqs.Remove(f);
        await _db.SaveChangesAsync(ct);
        await LogAsync(nameof(SiteFaq), id, created: false, ct, deleted: true);
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// The single content row, created on first use.
    ///
    /// The defaults describe this platform in general terms rather than leaving a blank page:
    /// they are a starting point to edit, and they claim nothing that is not true of any auction
    /// platform. No figure, review or customer name is invented here.
    /// </summary>
    private async Task<SiteContent> GetSettingsEntityAsync(CancellationToken ct)
    {
        var existing = await _db.SiteContent.FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        var seeded = new SiteContent
        {
            HeroTitle = "زايد… واربح",
            HeroSubtitle = "اشترك في المزاد، زايد على المنتج، واحصل عليه بأفضل سعر. "
                + "ومن لم يفز يسترد رسوم اشتراكه رصيدَ تسوق في المتجر.",
            HeroCtaLabel = "حمّل التطبيق",

            AboutEyebrow = "من نحن",
            AboutTitle = "منصة مزادات واضحة من أولها لآخرها",
            FeaturesTitle = "مميزاتنا",
            FeaturesBody = "مزادات مباشرة تُعرض لحظة بلحظة، وأسعار تتحرك أمامك، "
                + "ومحفظتان منفصلتان دائماً: رصيد نقدي ورصيد تسوق لا يختلطان.",
            VisionTitle = "رؤيتنا",
            VisionBody = "أن تكون المزايدة تجربة ممتعة وعادلة، يعرف فيها كل مشارك "
                + "إلى أين ذهب مالُه وماذا حصل عليه في المقابل.",

            StatUsersLabel = "مستخدم",
            StatAuctionsLabel = "مزاد",
            StatProductsLabel = "منتج في المتجر",

            StepsEyebrow = "طريقة الاستخدام",
            StepsTitle = "ابدأ في ثلاث خطوات",

            ParticipateEyebrow = "كيف تشارك",
            ParticipateTitle = "من الاشتراك إلى الاستلام",

            ShowcaseEyebrow = "من داخل التطبيق",
            ShowcaseTitle = "شاهد كيف تبدو التجربة",
            ShowcaseSubtitle = "ثلاث لقطات من التطبيق، وتحت كل واحدة شرحها.",

            TestimonialsEyebrow = "آراء المستخدمين",
            TestimonialsTitle = "ماذا قالوا عن التجربة",

            FaqEyebrow = "الأسئلة الشائعة",
            FaqTitle = "أسئلة يسألها الجميع",

            DownloadTitle = "التطبيق بين يديك",
            DownloadSubtitle = "حمّل التطبيق وابدأ أول مزاد لك اليوم.",

            MetaTitle = "DealGo — صفقتك جاهزة",
            MetaDescription = "منصة مزادات إلكترونية: شارك في المزاد، زايد، "
                + "واحصل على المنتج بأفضل سعر.",
        };

        _db.SiteContent.Add(seeded);

        SeedCardsAndQuestions();

        await _db.SaveChangesAsync(ct);
        return seeded;
    }

    /*
     * The starter cards and questions.
     *
     * Added only alongside the very first content row, so nothing an administrator later deletes
     * comes back. Every line describes how this platform actually behaves — the entry fee taken
     * from the Main wallet, the four ranked winners, the refund as store credit — rather than
     * making a claim about it. There is no equivalent block for reviews: those name real people.
     */
    private void SeedCardsAndQuestions()
    {
        var order = 0;

        void Step(string icon, string title, string body) =>
            _db.SiteCards.Add(new SiteCard
            {
                Kind = SiteCardKind.Step,
                Icon = icon,
                Title = title,
                Body = body,
                DisplayOrder = order++,
            });

        Step("search", "اختر المزاد",
            "تصفّح المزادات المفتوحة واختر المنتج الذي تريده.");
        Step("coins", "ادفع رسوم الاشتراك",
            "تُخصم رسوم الاشتراك مرة واحدة من محفظتك الرئيسية، وبها تدخل المزاد.");
        Step("gavel", "زايد حتى النهاية",
            "كل مزايدة ترفع السعر خطوة واحدة. عند انتهاء الوقت يفوز أصحاب "
            + "المراكز الأربعة الأولى بالجوائز.");

        order = 0;

        void Card(string icon, string title, string body) =>
            _db.SiteCards.Add(new SiteCard
            {
                Kind = SiteCardKind.Participate,
                Icon = icon,
                Title = title,
                Body = body,
                DisplayOrder = order++,
            });

        Card("wallet", "اشحن محفظتك", "أضف رصيداً إلى المحفظة الرئيسية.");
        Card("users", "اشترك", "ادخل المزاد بدفع رسوم الاشتراك.");
        Card("gavel", "زايد", "ارفع السعر وتابع ترتيبك لحظة بلحظة.");
        Card("gift", "استلم", "إن فزت تصلك الجائزة، وإن لم تفز يعود إليك رصيد تسوق.");

        order = 0;

        void Ask(string question, string answer) =>
            _db.SiteFaqs.Add(new SiteFaq
            {
                Question = question,
                Answer = answer,
                DisplayOrder = order++,
            });

        Ask("كيف أشارك في المزاد؟",
            "اشحن محفظتك الرئيسية، ثم افتح المزاد الذي تريده واضغط على الاشتراك. "
            + "تُخصم رسوم الاشتراك مرة واحدة، وبعدها يمكنك المزايدة.");

        Ask("ماذا يحدث إذا لم أفز؟",
            "تعود إليك رسوم اشتراكك كاملةً على هيئة رصيد تسوق تشتري به من المتجر. "
            + "لا تذهب الرسوم هدراً.");

        Ask("هل يمكنني سحب رصيد التسوق نقداً؟",
            "لا. رصيد التسوق للشراء من المتجر فقط وغير قابل للسحب. "
            + "أما المحفظة الرئيسية فهي رصيد نقدي يمكن سحبه.");

        Ask("من الذي يفوز بالجوائز؟",
            "أصحاب المراكز الأربعة الأولى حسب أعلى سعر وصلوا إليه. "
            + "لكل مزاد أربع جوائز مرتبة، والمركز الأول يفوز بالمنتج الرئيسي.");

        Ask("هل تُخصم قيمة المزايدة من رصيدي؟",
            "لا. المزايدة تحرّك سعر المنتج فقط. الشيء الوحيد الذي يُخصم منك "
            + "هو رسوم الاشتراك، ومرة واحدة.");

        Ask("ماذا لو أُلغي المزاد؟",
            "تعود رسوم الاشتراك بالكامل إلى محفظتك الرئيسية نقداً، لا رصيدَ تسوق، "
            + "ويصل إشعار بذلك لكل مشارك.");
    }

    private Task LogAsync(string entity, Guid id, bool created, CancellationToken ct, bool deleted = false) =>
        _audit.LogAsync(
            AuditActionType.SiteContentChanged, entity, id,
            null, new { Action = deleted ? "Deleted" : created ? "Created" : "Updated" }, ct: ct);

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A published figure is never negative; an empty box means "use the real count".</summary>
    private static int? NonNegative(int? value) =>
        value is null or < 0 ? null : value;

    private static void Require(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, message);
        }
    }

    // ---------------------------------------------------------------- mapping

    private static SiteCardDto MapCard(SiteCard c) => new(
        c.Id, c.Kind.ToString(), c.Title, c.Body, c.Icon, c.ImageUrl, c.DisplayOrder, c.IsPublished);

    private static SiteTestimonialDto MapTestimonial(SiteTestimonial t) => new(
        t.Id, t.Name, t.Body, t.Rating, t.Role, t.AvatarUrl, t.DisplayOrder, t.IsPublished);

    private static SiteFaqDto MapFaq(SiteFaq f) => new(
        f.Id, f.Question, f.Answer, f.DisplayOrder, f.IsPublished);

    private static SiteSettingsDto MapSettings(SiteContent s) => new()
    {
        HeroBadge = s.HeroBadge,
        HeroTitle = s.HeroTitle,
        HeroSubtitle = s.HeroSubtitle,
        HeroCtaLabel = s.HeroCtaLabel,
        HeroCtaUrl = s.HeroCtaUrl,
        HeroImageUrl = s.HeroImageUrl,
        AppStoreUrl = s.AppStoreUrl,
        GooglePlayUrl = s.GooglePlayUrl,
        AboutEnabled = s.AboutEnabled,
        AboutEyebrow = s.AboutEyebrow,
        AboutTitle = s.AboutTitle,
        FeaturesTitle = s.FeaturesTitle,
        FeaturesBody = s.FeaturesBody,
        VisionTitle = s.VisionTitle,
        VisionBody = s.VisionBody,
        StatsEnabled = s.StatsEnabled,
        StatUsersOverride = s.StatUsersOverride,
        StatAuctionsOverride = s.StatAuctionsOverride,
        StatProductsOverride = s.StatProductsOverride,
        StatUsersLabel = s.StatUsersLabel,
        StatAuctionsLabel = s.StatAuctionsLabel,
        StatProductsLabel = s.StatProductsLabel,
        StepsEnabled = s.StepsEnabled,
        StepsEyebrow = s.StepsEyebrow,
        StepsTitle = s.StepsTitle,
        StepsSubtitle = s.StepsSubtitle,
        ParticipateEnabled = s.ParticipateEnabled,
        ParticipateEyebrow = s.ParticipateEyebrow,
        ParticipateTitle = s.ParticipateTitle,
        ParticipateSubtitle = s.ParticipateSubtitle,
        ShowcaseEnabled = s.ShowcaseEnabled,
        ShowcaseEyebrow = s.ShowcaseEyebrow,
        ShowcaseTitle = s.ShowcaseTitle,
        ShowcaseSubtitle = s.ShowcaseSubtitle,
        TestimonialsEnabled = s.TestimonialsEnabled,
        TestimonialsEyebrow = s.TestimonialsEyebrow,
        TestimonialsTitle = s.TestimonialsTitle,
        TestimonialsSubtitle = s.TestimonialsSubtitle,
        FaqEnabled = s.FaqEnabled,
        FaqEyebrow = s.FaqEyebrow,
        FaqTitle = s.FaqTitle,
        FaqSubtitle = s.FaqSubtitle,
        DownloadEnabled = s.DownloadEnabled,
        DownloadTitle = s.DownloadTitle,
        DownloadSubtitle = s.DownloadSubtitle,
        ContactEmail = s.ContactEmail,
        ContactPhone = s.ContactPhone,
        FooterNote = s.FooterNote,
        PrivacyUrl = s.PrivacyUrl,
        TermsUrl = s.TermsUrl,
        TwitterUrl = s.TwitterUrl,
        InstagramUrl = s.InstagramUrl,
        TiktokUrl = s.TiktokUrl,
        SnapchatUrl = s.SnapchatUrl,
        WhatsappUrl = s.WhatsappUrl,
        MetaTitle = s.MetaTitle,
        MetaDescription = s.MetaDescription,
    };
}
