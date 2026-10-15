namespace AuctionPlatform.Infrastructure.Persistence;

/// <summary>
/// The demonstration store: categories and products with real product photographs, so the app's
/// home page and category grids look like a shop rather than a wall of placeholders.
///
/// Development data only — the seeder never runs it in Production. The pictures come from
/// DummyJSON's public test catalogue (cdn.dummyjson.com); an image is named by its
/// "category/product" path there. Prices are our own, in SAR; a device is called by its own
/// name, because the picture beside it is a picture of that device.
/// </summary>
internal static class DemoStoreCatalog
{
    private const string Cdn = "https://cdn.dummyjson.com/product-images";

    public static string ProductImage(string path) => $"{Cdn}/{path}/1.webp";
    public static string CategoryImage(string path) => $"{Cdn}/{path}/thumbnail.webp";

    public sealed record Product(string Image, string Name, decimal Price)
    {
        /// <summary>The picture's own name doubles as the slug; it is already unique and ASCII.</summary>
        public string Slug => Image[(Image.LastIndexOf('/') + 1)..];

        public string Sku => $"DEMO-{Slug.ToUpperInvariant()}";
    }

    public sealed record Category(string Slug, string Name, int DisplayOrder, string Image, Product[] Products);

    /// <summary>
    /// Pictures for the nine products the original seed created with random placeholder photos,
    /// keyed by SKU. Applied only while a product still has its placeholder.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> OriginalProductImages = new Dictionary<string, string>
    {
        ["SKU-HEADPHONES-001"] = "mobile-accessories/apple-airpods-max-silver",
        ["SKU-EARBUDS-001"] = "mobile-accessories/beats-flex-wireless-earphones",
        ["SKU-SPEAKER-001"] = "mobile-accessories/apple-homepod-mini-cosmic-grey",
        ["SKU-CASE-001"] = "mobile-accessories/iphone-12-silicone-case-with-magsafe-plum",
        ["SKU-CHARGER-001"] = "mobile-accessories/apple-iphone-charger",
        ["SKU-SLEEVE-001"] = "womens-bags/white-faux-leather-backpack",
        ["SKU-LAMP-001"] = "home-decoration/table-lamp",
        ["SKU-GRINDER-001"] = "kitchen-accessories/boxed-blender",
    };

    public static readonly Category[] Categories =
    {
        // The first three already exist in a seeded database; they gain a picture and more items.
        new("electronics", "إلكترونيات", 1, "laptops/huawei-matebook-x-pro", new Product[]
        {
            new("smartphones/iphone-13-pro", "آيفون 13 برو · 256 جيجابايت", 4125m),
            new("smartphones/iphone-x", "آيفون X · 64 جيجابايت", 3375m),
            new("smartphones/samsung-galaxy-s10", "سامسونج جالاكسي S10", 2625m),
            new("smartphones/samsung-galaxy-s8", "سامسونج جالاكسي S8", 1875m),
            new("smartphones/oppo-f19-pro-plus", "أوبو F19 Pro+", 1499m),
            new("smartphones/vivo-x21", "فيفو X21", 1875m),
            new("smartphones/realme-xt", "ريلمي XT", 1310m),
            new("smartphones/realme-c35", "ريلمي C35", 560m),
            new("laptops/apple-macbook-pro-14-inch-space-grey", "ماك بوك برو 14 بوصة · رمادي", 7499m),
            new("laptops/asus-zenbook-pro-dual-screen-laptop", "أسوس زينبوك برو ديو · شاشتان", 6749m),
            new("laptops/huawei-matebook-x-pro", "هواوي ميت بوك X برو", 5249m),
            new("laptops/lenovo-yoga-920", "لينوفو يوجا 920", 4125m),
            new("laptops/new-dell-xps-13-9300-laptop", "ديل XPS 13 · 9300", 5620m),
            new("tablets/ipad-mini-2021-starlight", "آيباد ميني 2021 · ستارلايت", 1875m),
            new("tablets/samsung-galaxy-tab-s8-plus-grey", "جالاكسي تاب S8 بلس", 2250m),
            new("tablets/samsung-galaxy-tab-white", "جالاكسي تاب · أبيض", 1310m),
        }),
        new("accessories", "إكسسوارات", 2, "mobile-accessories/apple-airpods", new Product[]
        {
            new("mobile-accessories/apple-airpods", "سماعات Apple AirPods", 487m),
            new("mobile-accessories/apple-airpower-wireless-charger", "شاحن Apple اللاسلكي", 299m),
            new("mobile-accessories/apple-magsafe-battery-pack", "بطارية MagSafe من Apple", 375m),
            new("mobile-accessories/amazon-echo-plus", "أمازون إيكو بلس", 375m),
            new("mobile-accessories/monopod", "حامل تصوير أحادي", 75m),
            new("mobile-accessories/selfie-lamp-with-iphone", "إضاءة سيلفي", 56m),
            new("mobile-accessories/selfie-stick-monopod", "عصا سيلفي", 49m),
            new("mobile-accessories/tv-studio-camera-pedestal", "حامل كاميرا احترافي", 1875m),
        }),
        new("home", "المنزل", 3, "furniture/annibale-colombo-sofa", new Product[]
        {
            new("furniture/annibale-colombo-sofa", "كنبة فاخرة", 9375m),
            new("furniture/annibale-colombo-bed", "سرير مزدوج", 7125m),
            new("furniture/bedside-table-african-cherry", "طاولة جانبية", 1125m),
            new("furniture/knoll-saarinen-executive-conference-chair", "كرسي مكتب", 1875m),
            new("furniture/wooden-bathroom-sink-with-mirror", "مغسلة خشبية بمرآة", 2999m),
            new("home-decoration/decoration-swing", "أرجوحة ديكور", 225m),
            new("home-decoration/family-tree-photo-frame", "إطار صور عائلي", 112m),
            new("home-decoration/house-showpiece-plant", "نبتة زينة", 150m),
            new("home-decoration/plant-pot", "أصيص نباتات", 56m),
        }),
        new("kitchen", "المطبخ", 4, "kitchen-accessories/silver-pot-with-glass-cap", new Product[]
        {
            new("kitchen-accessories/microwave-oven", "فرن ميكروويف", 337m),
            new("kitchen-accessories/electric-stove", "موقد كهربائي", 187m),
            new("kitchen-accessories/hand-blender", "خلاط يدوي", 131m),
            new("kitchen-accessories/silver-pot-with-glass-cap", "قدر بغطاء زجاجي", 150m),
            new("kitchen-accessories/carbon-steel-wok", "مقلاة ووك", 112m),
            new("kitchen-accessories/pan", "مقلاة", 94m),
            new("kitchen-accessories/knife", "سكين مطبخ", 56m),
            new("kitchen-accessories/chopping-board", "لوح تقطيع", 49m),
            new("kitchen-accessories/spice-rack", "رف توابل", 75m),
            new("kitchen-accessories/lunch-box", "علبة طعام", 49m),
            new("kitchen-accessories/mug-tree-stand", "حامل أكواب", 60m),
            new("kitchen-accessories/wooden-rolling-pin", "شوبك خشبي", 45m),
        }),
        new("watches", "ساعات", 5, "mens-watches/longines-master-collection", new Product[]
        {
            new("mens-watches/brown-leather-belt-watch", "ساعة بحزام جلد بني", 337m),
            new("mens-watches/longines-master-collection", "لونجين Master Collection", 5625m),
            new("mens-watches/rolex-cellini-date-black-dial", "رولكس Cellini Date · ميناء أسود", 8999m),
            new("mens-watches/rolex-datejust", "رولكس Datejust", 9499m),
            new("mens-watches/rolex-submariner-watch", "رولكس Submariner", 9999m),
            new("mens-watches/rolex-cellini-moonphase", "رولكس Cellini Moonphase", 9899m),
            new("womens-watches/iwc-ingenieur-automatic-steel", "IWC Ingenieur أوتوماتيك", 7499m),
            new("womens-watches/rolex-datejust-women", "رولكس Datejust نسائية", 8999m),
            new("womens-watches/watch-gold-for-women", "ساعة نسائية ذهبية", 2999m),
            new("mobile-accessories/apple-watch-series-4-gold", "ساعة Apple Watch Series 4 · ذهبية", 1310m),
        }),
        new("beauty", "عطور وتجميل", 6, "fragrances/chanel-coco-noir-eau-de", new Product[]
        {
            new("fragrances/calvin-klein-ck-one", "عطر منعش للجنسين", 187m),
            new("fragrances/chanel-coco-noir-eau-de", "عطر نسائي فاخر", 487m),
            new("fragrances/dolce-shine-eau-de", "عطر زهري", 262m),
            new("fragrances/gucci-bloom-eau-de", "عطر الورد", 299m),
            new("beauty/essence-mascara-lash-princess", "ماسكارا", 37m),
            new("beauty/eyeshadow-palette-with-mirror", "لوحة ظلال عيون", 75m),
            new("beauty/powder-canister", "بودرة وجه", 56m),
            new("beauty/red-lipstick", "أحمر شفاه", 49m),
            new("beauty/red-nail-polish", "طلاء أظافر", 34m),
            new("skin-care/olay-ultra-moisture-shea-butter-body-wash", "غسول جسم بزبدة الشيا", 49m),
            new("skin-care/vaseline-men-body-and-face-lotion", "لوشن للوجه والجسم", 37m),
            new("skin-care/attitude-super-leaves-hand-soap", "صابون يدين طبيعي", 34m),
        }),
        new("men-fashion", "أزياء رجالية", 7, "mens-shirts/man-plaid-shirt", new Product[]
        {
            new("mens-shirts/man-plaid-shirt", "قميص كاروهات", 131m),
            new("mens-shirts/man-short-sleeve-shirt", "قميص بأكمام قصيرة", 75m),
            new("mens-shirts/men-check-shirt", "قميص مربعات", 105m),
            new("mens-shirts/gigabyte-aorus-men-tshirt", "تيشيرت رجالي", 94m),
            new("mens-shoes/nike-air-jordan-1-red-and-black", "نايك إير جوردن 1 · أحمر وأسود", 562m),
            new("mens-shoes/nike-baseball-cleats", "نايك Baseball Cleats", 299m),
            new("mens-shoes/puma-future-rider-trainers", "حذاء جري", 337m),
            new("mens-shoes/sports-sneakers-off-white-red", "حذاء رياضي أبيض", 412m),
            new("sunglasses/black-sun-glasses", "نظارة شمسية سوداء", 112m),
            new("sunglasses/classic-sun-glasses", "نظارة شمسية كلاسيكية", 94m),
        }),
        new("women-fashion", "أزياء نسائية", 8, "tops/girl-summer-dress", new Product[]
        {
            new("tops/blue-frock", "فستان أزرق", 112m),
            new("tops/girl-summer-dress", "فستان صيفي", 75m),
            new("tops/gray-dress", "فستان رمادي", 131m),
            new("tops/short-frock", "فستان قصير", 94m),
            new("tops/tartan-dress", "فستان كاروهات", 150m),
            new("womens-dresses/dress-pea", "فستان أخضر", 187m),
            new("womens-dresses/corset-with-black-skirt", "طقم بتنورة سوداء", 299m),
            new("womens-dresses/corset-leather-with-skirt", "طقم جلدي بتنورة", 337m),
            new("womens-shoes/golden-shoes-woman", "حذاء نسائي ذهبي", 187m),
            new("womens-shoes/red-shoes", "حذاء نسائي أحمر", 131m),
            new("womens-shoes/pampi-shoes", "حذاء نسائي مريح", 112m),
            new("womens-shoes/calvin-klein-heel-shoes", "حذاء بكعب عالٍ", 299m),
        }),
        new("bags-jewellery", "حقائب ومجوهرات", 9, "womens-bags/prada-women-bag", new Product[]
        {
            new("womens-bags/women-handbag-black", "حقيبة يد سوداء", 225m),
            new("womens-bags/prada-women-bag", "حقيبة جلد فاخرة", 2249m),
            new("womens-jewellery/green-crystal-earring", "أقراط كريستال خضراء", 112m),
            new("womens-jewellery/green-oval-earring", "أقراط بيضاوية", 94m),
            new("womens-jewellery/tropical-earring", "أقراط استوائية", 75m),
            new("sunglasses/green-and-black-glasses", "نظارة خضراء وسوداء", 131m),
            new("sunglasses/party-glasses", "نظارة حفلات", 75m),
            new("sunglasses/sunglasses", "نظارة شمسية عصرية", 86m),
        }),
        new("sports", "رياضة", 10, "sports-accessories/football", new Product[]
        {
            new("sports-accessories/football", "كرة قدم", 67m),
            new("sports-accessories/basketball", "كرة سلة", 56m),
            new("sports-accessories/volleyball", "كرة طائرة", 45m),
            new("sports-accessories/tennis-racket", "مضرب تنس", 187m),
            new("sports-accessories/tennis-ball", "كرات تنس", 26m),
            new("sports-accessories/basketball-rim", "طوق كرة سلة", 150m),
            new("sports-accessories/iron-golf", "مضرب غولف", 187m),
            new("sports-accessories/golf-ball", "كرات غولف", 37m),
            new("sports-accessories/american-football", "كرة قدم أمريكية", 75m),
            new("sports-accessories/baseball-glove", "قفاز بيسبول", 94m),
            new("sports-accessories/cricket-bat", "مضرب كريكيت", 112m),
            new("sports-accessories/feather-shuttlecock", "ريشة تنس", 22m),
        }),
        new("groceries", "بقالة", 11, "groceries/strawberry", new Product[]
        {
            new("groceries/strawberry", "فراولة", 15m),
            new("groceries/apple", "تفاح", 7m),
            new("groceries/kiwi", "كيوي", 9m),
            new("groceries/lemon", "ليمون", 3m),
            new("groceries/potatoes", "بطاطس", 9m),
            new("groceries/red-onions", "بصل أحمر", 7m),
            new("groceries/green-bell-pepper", "فلفل أخضر", 5m),
            new("groceries/honey-jar", "عسل طبيعي", 26m),
            new("groceries/milk", "حليب", 13m),
            new("groceries/eggs", "بيض", 11m),
            new("groceries/rice", "أرز", 22m),
            new("groceries/juice", "عصير", 15m),
        }),
    };
}
