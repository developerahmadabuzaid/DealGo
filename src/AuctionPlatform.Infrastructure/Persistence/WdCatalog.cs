namespace AuctionPlatform.Infrastructure.Persistence;

/// <summary>
/// The WD range: the products this store actually sells.
///
/// Generated from the supplier's price sheet ("سعر بيع ايثاق.xlsx"), so every price and every
/// description is the sheet's own. Two things were decided rather than copied:
///
///   * <b>The price is the retail column</b>, "سعر البيع بالسعودي". The sheet's other column, "سعر
///     ايثاق بعد الخصم", is what the store pays WD — 15% below retail — and showing it would be
///     selling at cost. The supplier's note at the foot of the sheet asks shops to sell at the
///     invoiced price, so no discount is invented either.
///   * <b>The short name is ours.</b> The sheet carries one long paragraph per item, which reads
///     well on a product page and badly on a tile three to a row; the paragraph is kept as the
///     description.
///
/// The pictures ship inside the API image under wwwroot/catalog rather than being uploaded, so
/// they cannot be lost with a container and need no storage account to serve.
/// </summary>
public static class WdCatalog
{
    /// <summary>Every SKU in this range starts with one of these; it is how a WD product is told apart.</summary>
    public static bool IsWdSku(string sku) =>
        sku.StartsWith("W-X", StringComparison.OrdinalIgnoreCase) ||
        sku.StartsWith("WD-", StringComparison.OrdinalIgnoreCase);

    public sealed record Product(string Sku, string Name, decimal Price, string Image, string Description)
    {
        public string Slug => Sku.ToLowerInvariant();
    }

    public sealed record Category(string Slug, string Name, int DisplayOrder, Product[] Products)
    {
        /// <summary>A department's picture is its first product's: no separate artwork to keep.</summary>
        public string Image => Products[0].Image;
    }

    public static readonly IReadOnlyList<Category> Categories = new Category[]
    {
        new("wd-cables", "كيابل الشحن", 1, new Product[]
        {
            new("W-X10", "كيبل USB إلى Type-C مضفّر 3A", 12.50m, "/catalog/w-x10.webp",
                "كيبل( usb.t)3A مضفر خام مصنوع من سبائك الالمنيوم معزز بحمايه ضد القطع عند الأطراف الطول 1متر"),
            new("W-X11", "كيبل USB إلى Lightning مضفّر 3A", 12.50m, "/catalog/w-x11.webp",
                "كيبل(usb.L)3A مضفر خام مصنوع من سبائك الالمنيوم معزز بحمايه ضد القطع عند الأطراف الطول 1متر"),
            new("W-X12", "كيبل Type-C إلى Type-C مضفّر 60W", 12.50m, "/catalog/w-x12.webp",
                "كيبل(C to c)60W مضفرخام مصنوع من سبائك الالمنيوم معزز بحمايه ضد القطع عند الأطراف الطول 1متر"),
            new("W-X13", "كيبل Type-C إلى Lightning مضفّر 27W", 12.50m, "/catalog/w-x13.webp",
                "كيبل( C to l) 27Wمضفر مصنوع من سبائك الالمنيوم معزز بحمايه ضد القطع عند الأطراف الطول 1متر"),
            new("W-X14", "كيبل USB إلى Type-C سيليكون 3A", 15.50m, "/catalog/w-x14.webp",
                "كيبل 3A (usb.t) شاحن سريع مصنوع من السيلكون مضاد للقطع ومقاوم للحريق معزز بحمايه ضد القطع عند الأطراف الطول 1متر"),
            new("W-X15", "كيبل USB إلى Lightning سيليكون 3A", 15.50m, "/catalog/w-x15.webp",
                "كيبل 3A(USB. L) شاحن سريع مصنوع من السيلكون مضاد للقطع .ومقاوم للحريق معزز بحمايه ضد القطع عند الأطراف الطول 1متر"),
            new("W-X16", "كيبل Type-C إلى Type-C سيليكون 60W", 15.50m, "/catalog/w-x16.webp",
                "كيبل (CTO C) 60Wمصنوع من السيلكون مضاد للقطع .ومقاوم للحريق معزز بحمايه ضد القطع للأطراف 1منر"),
            new("W-X17", "كيبل Type-C إلى Lightning سيليكون 27W", 15.50m, "/catalog/w-x17.webp",
                "كيبل (CTO L)27W مصنوع من السيلكون مضاد للقطع . مقاوم للحريق معزز بحمايه ضد القطع عند الأطراف 1متر"),
            new("W-X18", "كيبل 4 في 1 بقدرة 240W", 18.50m, "/catalog/w-x18.webp",
                "كيبل (4IN1)240W رأس المنتج مصنوع من سبيكه ألمنيوم قويه وثقيله مصنوع من السيلكون أداء قوي مخصص نقل معلومات والشحن . 1متر"),
        }),

        new("wd-wall-chargers", "شواحن جدارية", 2, new Product[]
        {
            new("W-X21", "شاحن جداري GaN بقدرة 45W", 34.00m, "/catalog/w-x21.webp",
                "45W GaN شاحن قوي جدا يحتوي علي مخرجيين مصنع من مواد متطورة تتحمل الحرارة العالية لمنع الحرائق.نظام حماية مدمج ضد الصدمات تدعم شحناً فائق السرعة وبعض أجهزة اللابتوب الصغيرة."),
            new("W-X22", "شاحن جداري 45W مع كيبل Lightning", 40.00m, "/catalog/w-x22.webp",
                "45W GaN شاحن كامل قوي 45واط مخرجين مصنوع من مواد مقاومة للحريق وتقنيات تمنع ارتفاع درجة حرارة الشاحن. يحتوي على نظام يحمي جهازك من (الالتماس الكهربائي، التيار الزائد، والشحن الزائد)"),
            new("W-X23", "شاحن جداري 45W مع كيبل Type-C", 40.00m, "/catalog/w-x23.webp",
                "45W GaN شاحن كامل 45واط مخرجيين مصنوع من مواد متطورة مضادة للحريق وتمنع ارتفاع درجة الحرارة أثناء الشحن.شاحن ذكي متوافق مع أغلب الأجهزة ومصنوع من مواد متينة لضمان استقرار الشحن."),
            new("W-X24", "شاحن جداري GaN بمنفذي Type-C 45W", 37.00m, "/catalog/w-x24.webp",
                "45W GaN شاحن قوي 45واط مخرجين شاحن ذكي مصنوع من مواد قوية متوافقة مع الأجهزة الحديثة لضمان شحن مستقر.هذا الشاحن ممتاز جداً لهواتف سامسونج (فئة S) وآيفون الحديثة، وحتى لبعض أجهزة اللابتوب اللوحية."),
            new("W-X25", "شاحن جداري GaN بقدرة 65W", 52.00m, "/catalog/w-x25.webp",
                "65W PD GaN شاحن بقدرة عاليه 65واط مصنوع من مواد مقاومة للحريق وتدعم تقنيات منع ارتفاع درجة الحرارة لزيادة كفاءة الشحن.شاحن ذكي متوافق مع الأجهزة المختلفة، ومصنوع من بلاستيك (PC) مستورد لضمان شحن آمن ومستقر."),
        }),

        new("wd-car-chargers", "شواحن السيارة", 3, new Product[]
        {
            new("W-X26", "شاحن سيارة 48W مع كيبل Lightning", 24.50m, "/catalog/w-x26.webp",
                "PD30W+QC18W =48W شاحن كامل سياره مخرجين 48واط مصنوع من مواد متطورة وتقنيات تمنع ارتفاع درجة الحرارة لزيادة كفاءة الشحن.خضع الشاحن لرقابة دقيقة لضمان شحن آمن."),
            new("W-X27", "شاحن سيارة 48W مع كيبل Type-C", 24.50m, "/catalog/w-x27.webp",
                "PD30W+QC18W =48W شاحن كامل سياره مخرجين 48واط مصنوع من مواد متطورة وتقنيات تمنع ارتفاع درجة الحرارة لزيادة كفاءة الشحن.خضع الشاحن لرقابة دقيقة لضمان شحن آمن."),
            new("W-X28", "شاحن سيارة 65W مع كيبل Lightning", 34.00m, "/catalog/w-x28.webp",
                "PD35W+30W =65W شاحن سياره بقدره عاليه جدا 65واط شاحن ذكي بمتانة عالية، مصمم ليكون متوافقاً مع مختلف الأجهزة ويوفر شحناً مستقراً.خضع لاختبارات صارمة"),
        }),

        new("wd-earphones", "السماعات", 4, new Product[]
        {
            new("WD-E1", "سماعة سلكية Lightning", 15.50m, "/catalog/wd-e1.webp",
                "سماعه سلك مصممة لتناسب الأذن بشكل مثالي للاستخدام الطويل دون ألم.تدعم تقنية Hi-Fi وتصميم \"Ergonomic\" لتقديم تجربة صوتية عالية الجودة.سلك متين: الأسلاك الداخلية مصنوعة من النحاس الخالي من الأكسجين، مما يوفر مرونة ويمنع التشابك."),
            new("WD-E2", "سماعة سلكية Type-C", 18.50m, "/catalog/wd-e2.webp",
                "سماعه Type-cسلك مصممة لتناسب الأذن بشكل مثالي للاستخدام الطويل دون ألم.تدعم تقنية Hi-Fi وتصميم \"Ergonomic\" لتقديم تجربة صوتية عالية الجودة.سلك متين: الأسلاك الداخلية مصنوعة من النحاس الخالي من الأكسجين، مما يوفر مرونة ويمنع التشابك."),
        }),
    };
}
