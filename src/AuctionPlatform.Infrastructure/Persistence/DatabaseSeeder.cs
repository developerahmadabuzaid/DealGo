using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Domain.Rules;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionPlatform.Infrastructure.Persistence;

/// <summary>
/// Applies migrations and seeds reference data.
///
/// Roles and store settings are seeded in every environment because the application cannot work
/// without them. Demo users, products and auctions are seeded only when explicitly requested,
/// so production never receives development credentials.
/// </summary>
public sealed class DatabaseSeeder
{
    /// <summary>
    /// Names the seeder looks products up by. They are referenced in more than one place, so a
    /// typo would silently seed an auction with no prizes rather than fail.
    /// </summary>
    private static class ProductNames
    {
        public const string Iphone = "آيفون 13 برو · 256 جيجابايت";
        public const string Macbook = "ماك بوك برو 14 بوصة · M3";
        public const string Rolex = "ساعة Rolex Submariner";
        public const string Galaxy = "سامسونج جالاكسي S10";
        public const string Tablet = "جالاكسي تاب S8 بلس";
        public const string AppleWatch = "ساعة Apple Watch Series 4";
        public const string Airpods = "سماعات Apple AirPods";
        public const string Ipad = "آيباد ميني 2021";
    }

    private const string DemoPassword = "Passw0rd!";

    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleManager<ApplicationRole> _roles;
    private readonly INumberGenerator _numbers;
    private readonly ICacheService _cache;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        ApplicationDbContext db,
        UserManager<ApplicationUser> users,
        RoleManager<ApplicationRole> roles,
        INumberGenerator numbers,
        ICacheService cache,
        ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _users = users;
        _roles = roles;
        _numbers = numbers;
        _cache = cache;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await _db.Database.MigrateAsync(ct);
        _logger.LogInformation("Database migrations applied.");
    }

    /// <summary>Roles and the single store-settings row. Safe and required everywhere.</summary>
    public async Task SeedEssentialsAsync(CancellationToken ct = default)
    {
        foreach (var role in Roles.All)
        {
            if (!await _roles.RoleExistsAsync(role))
            {
                await _roles.CreateAsync(new ApplicationRole(role));
            }
        }

        if (!await _db.StoreSettings.AnyAsync(ct))
        {
            _db.StoreSettings.Add(new StoreSettings());
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Essential seed data verified.");
    }

    /// <summary>
    /// Makes the named account the first super administrator, once.
    ///
    /// Production creates no accounts, and only a super administrator can grant roles, so a fresh
    /// deployment had no way to get its first administrator. This closes that gap without putting
    /// a password anywhere near configuration: the person signs up through the app like anyone
    /// else, choosing their own password, and on the next start the account named here is
    /// promoted.
    ///
    /// It acts only while nobody holds the role. The moment a super administrator exists, the
    /// setting does nothing, so leaving it configured cannot be used later to hand the role to
    /// whoever manages to register that address.
    /// </summary>
    /// <remarks>
    /// The account may be named by email or by Saudi mobile number. People sign up in the app with
    /// their phone, so the number is usually the only identifier the first administrator has.
    /// </remarks>
    /// <returns>True when an account was promoted.</returns>
    public async Task<bool> PromoteBootstrapSuperAdminAsync(
        string? email, string? phone = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone))
        {
            return false;
        }

        if ((await _users.GetUsersInRoleAsync(Roles.SuperAdmin)).Count > 0)
        {
            _logger.LogInformation("A super administrator already exists; bootstrap setting ignored.");
            return false;
        }

        ApplicationUser? user = null;

        if (!string.IsNullOrWhiteSpace(email))
        {
            user = await _users.FindByEmailAsync(email.Trim());
        }

        if (user is null && !string.IsNullOrWhiteSpace(phone))
        {
            if (!SaudiPhone.TryNormalise(phone, out var canonical))
            {
                _logger.LogWarning("Bootstrap:SuperAdminPhone is not a Saudi mobile number; it was ignored.");
            }
            else
            {
                user = await _db.Users.FirstOrDefaultAsync(u => u.PhoneNumber == canonical, ct);
            }
        }

        if (user is null)
        {
            _logger.LogWarning(
                "No super administrator exists yet. Sign up in the app with the bootstrap phone number " +
                "(or register the bootstrap email), then restart the app to promote it.");
            return false;
        }

        var result = await _users.AddToRoleAsync(user, Roles.SuperAdmin);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not promote the bootstrap account: " +
                string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        _logger.LogWarning("Account {UserId} was promoted to super administrator by the bootstrap setting.", user.Id);
        return true;
    }

    /// <summary>
    /// Demo data for development and testing. Never call this in production: the accounts use a
    /// well-known password.
    /// </summary>
    public async Task SeedDevelopmentDataAsync(CancellationToken ct = default)
    {
        if (await _db.Auctions.AnyAsync(ct))
        {
            // The demo store is topped up even on an existing database, so new demo items reach
            // it without a reset.
            await SeedDemoStoreAsync(ct);

            _logger.LogInformation("Development data already present; skipping.");
            return;
        }

        var superAdmin = await EnsureUserAsync("superadmin@auctions.local", "Super Admin", Roles.SuperAdmin);
        await EnsureUserAsync("admin@auctions.local", "Platform Admin", Roles.Admin);
        await EnsureUserAsync("store@auctions.local", "Store Manager", Roles.StoreManager);
        await EnsureUserAsync("finance@auctions.local", "Finance Officer", Roles.Finance);

        var bidders = new List<ApplicationUser>();
        for (var i = 1; i <= 12; i++)
        {
            var user = await EnsureUserAsync($"user{i}@auctions.local", $"Test User {i}", Roles.User);
            bidders.Add(user);

            await CreditWalletAsync(user.Id, WalletType.Main, 500m, ct);
        }

        await SeedCatalogProductsAsync(ct);
        await SeedStoreAsync(ct);
        await SeedDemoStoreAsync(ct);
        await SeedAuctionsAsync(superAdmin.Id, ct);

        _logger.LogInformation(
            "Development data seeded. Demo accounts use the password {Password}.", DemoPassword);
    }

    private async Task<ApplicationUser> EnsureUserAsync(string email, string fullName, string role)
    {
        var user = await _users.FindByEmailAsync(email);

        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = fullName,
                Language = "ar",
                IsActive = true
            };

            var result = await _users.CreateAsync(user, DemoPassword);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not seed {email}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
            }
        }

        if (!await _users.IsInRoleAsync(user, role))
        {
            await _users.AddToRoleAsync(user, role);
        }

        // Everyone gets both wallets.
        foreach (var type in new[] { WalletType.Main, WalletType.Shopping })
        {
            if (!await _db.Wallets.AnyAsync(w => w.UserId == user.Id && w.WalletType == type))
            {
                _db.Wallets.Add(new Wallet
                {
                    UserId = user.Id,
                    WalletType = type,
                    Balance = 0m,
                    Currency = Money.DefaultCurrency
                });
            }
        }

        await _db.SaveChangesAsync();
        return user;
    }

    private async Task CreditWalletAsync(Guid userId, WalletType type, decimal amount, CancellationToken ct)
    {
        var wallet = await _db.Wallets.FirstAsync(w => w.UserId == userId && w.WalletType == type, ct);
        if (wallet.Balance >= amount) return;

        var before = wallet.Balance;
        wallet.Balance = amount;
        wallet.UpdatedAt = DateTime.UtcNow;

        _db.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = wallet.Id,
            UserId = userId,
            WalletType = type,
            TransactionType = WalletTransactionType.Deposit,
            Amount = amount - before,
            Currency = wallet.Currency,
            BalanceBefore = before,
            BalanceAfter = amount,
            Description = "إيداع تجريبي",
            ReferenceType = "Seed"
        });

        await _db.SaveChangesAsync(ct);
    }

    private async Task SeedCatalogProductsAsync(CancellationToken ct)
    {
        if (await _db.Products.AnyAsync(ct)) return;

        /*
         * Real devices, with the photograph of that same device beside the name. The pictures come
         * from the same public catalogue the demonstration store uses, so a prize on the front page
         * looks like the thing somebody is bidding for rather than a random stock photograph.
         */
        var products = new[]
        {
            Demo(ProductNames.Rolex, 29_900m, "mens-watches/rolex-submariner-watch"),
            Demo(ProductNames.Macbook, 9_499m, "laptops/apple-macbook-pro-14-inch-space-grey"),
            Demo(ProductNames.Iphone, 4_999m, "smartphones/iphone-13-pro"),
            Demo(ProductNames.Tablet, 3_299m, "tablets/samsung-galaxy-tab-s8-plus-grey"),
            Demo(ProductNames.Ipad, 2_499m, "tablets/ipad-mini-2021-starlight"),
            Demo(ProductNames.Galaxy, 1_999m, "smartphones/samsung-galaxy-s10"),
            Demo(ProductNames.AppleWatch, 1_299m, "mobile-accessories/apple-watch-series-4-gold"),
            Demo(ProductNames.Airpods, 899m, "mobile-accessories/apple-airpods")
        };

        _db.Products.AddRange(products);
        await _db.SaveChangesAsync(ct);

        static Product Demo(string name, decimal price, string image) => new()
        {
            Name = name,
            Price = price,
            PrimaryImageUrl = DemoStoreCatalog.ProductImage(image)
        };
    }

    private async Task SeedStoreAsync(CancellationToken ct)
    {
        if (await _db.StoreCategories.AnyAsync(ct)) return;

        var categories = new[]
        {
            new StoreCategory { Name = "إلكترونيات", Slug = "electronics", DisplayOrder = 1 },
            new StoreCategory { Name = "إكسسوارات", Slug = "accessories", DisplayOrder = 2 },
            new StoreCategory { Name = "المنزل", Slug = "home", DisplayOrder = 3 }
        };
        _db.StoreCategories.AddRange(categories);
        await _db.SaveChangesAsync(ct);

        // The slug is carried separately rather than derived from the name: slugs travel in URLs
        // and must stay ASCII, which an Arabic name is not.
        var items = new (string Name, string Slug, string Sku, decimal Price, int Stock, int Category, bool Featured)[]
        {
            ("سماعات رأس برو", "headphones-pro", "SKU-HEADPHONES-001", 35m, 40, 0, true),
            ("سماعات لاسلكية", "wireless-earbuds", "SKU-EARBUDS-001", 25m, 60, 0, true),
            ("مكبر صوت بلوتوث", "bluetooth-speaker", "SKU-SPEAKER-001", 45m, 25, 0, false),
            ("غطاء هاتف", "phone-case", "SKU-CASE-001", 12m, 200, 1, false),
            ("شاحن سريع", "fast-charger", "SKU-CHARGER-001", 18m, 120, 1, true),
            ("حقيبة حاسب محمول", "laptop-sleeve", "SKU-SLEEVE-001", 22m, 80, 1, false),
            ("مصباح مكتب", "desk-lamp", "SKU-LAMP-001", 30m, 45, 2, false),
            ("مطحنة قهوة", "coffee-grinder", "SKU-GRINDER-001", 55m, 18, 2, true),
            ("ميزان مطبخ", "kitchen-scale", "SKU-SCALE-001", 20m, 3, 2, false)
        };

        foreach (var item in items)
        {
            var product = new StoreProduct
            {
                Name = item.Name,
                Slug = item.Slug,
                Description = $"{item.Name} — متاح للشراء برصيد التسوق.",
                CategoryId = categories[item.Category].Id,
                Sku = item.Sku,
                Price = item.Price,
                Currency = Money.DefaultCurrency,
                StockQuantity = item.Stock,
                Status = item.Stock > 0 ? StoreProductStatus.Active : StoreProductStatus.OutOfStock,
                PaymentMode = StorePaymentMode.Both,
                IsFeatured = item.Featured,
                RequiresShipping = true,
                ShippingFee = 0m
            };

            _db.StoreProducts.Add(product);

            _db.StoreProductImages.Add(new StoreProductImage
            {
                ProductId = product.Id,
                Url = $"https://picsum.photos/seed/{product.Slug}/600/600",
                IsPrimary = true,
                DisplayOrder = 0
            });

            _db.InventoryTransactions.Add(new InventoryTransaction
            {
                ProductId = product.Id,
                Type = InventoryTransactionType.Initial,
                Quantity = item.Stock,
                BeforeQuantity = 0,
                AfterQuantity = item.Stock,
                Reason = "رصيد افتتاحي"
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds the demo store's categories and products, with real product pictures. Safe to run on
    /// every start: a category is matched by slug and a product by SKU, and only what is missing is
    /// added. A category without a picture gets one, and an original demo product still showing
    /// its random placeholder gets a matching photo. Anything an administrator set is left alone.
    /// </summary>
    /// <summary>
    /// Puts the WD range in the store. Safe to run on every start, in every environment.
    ///
    /// <para>
    /// Every product is matched by SKU, so a second run adds only what is missing and never
    /// duplicates, and never overwrites a price or a stock level an administrator has since
    /// changed in the console.
    /// </para>
    /// <para>
    /// The <b>first</b> run also takes everything else off the shelf — the demonstration catalogue
    /// the store started with. That happens exactly once, decided by whether any WD product exists
    /// yet: after the first run they do, so the step never fires again. This matters because the
    /// alternative — "hide everything that is not WD" on every start — would silently hide any
    /// product an administrator adds through the console, on the very next deployment.
    /// </para>
    /// <para>
    /// Nothing is deleted. Products are archived and departments hidden: past orders still point
    /// at real rows, and either can be put back from the console.
    /// </para>
    /// </summary>
    public async Task SeedWdCatalogAsync(CancellationToken ct = default)
    {
        var existing = await _db.StoreProducts
            .Select(p => p.Sku)
            .ToListAsync(ct);

        var firstRun = !existing.Any(WdCatalog.IsWdSku);
        var skus = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wdSkus = WdCatalog.Categories
            .SelectMany(c => c.Products)
            .Select(p => p.Sku)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wdSlugs = WdCatalog.Categories.Select(c => c.Slug).ToHashSet();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        int archived = 0, hidden = 0;

        if (firstRun)
        {
            // Off the shelf, not out of the database: an order placed for one of these still
            // points at a real product, and the console can bring any of them back.
            archived = await _db.StoreProducts
                .Where(p => !wdSkus.Contains(p.Sku) && !p.Sku.StartsWith("AD-") &&
                            p.Status != StoreProductStatus.Archived)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, StoreProductStatus.Archived)
                    .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);

            // A department with nothing left in it would still draw a tab that opens onto
            // nothing, so the old departments go with their products.
            hidden = await _db.StoreCategories
                .Where(c => !wdSlugs.Contains(c.Slug) && !c.Slug.StartsWith("adomo-") &&
                            c.Status == StoreCategoryStatus.Active)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, StoreCategoryStatus.Hidden)
                    .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), ct);
        }

        var categories = await _db.StoreCategories
            .Where(c => wdSlugs.Contains(c.Slug))
            .ToDictionaryAsync(c => c.Slug, ct);

        var added = 0;

        foreach (var spec in WdCatalog.Categories)
        {
            if (!categories.TryGetValue(spec.Slug, out var category))
            {
                category = new StoreCategory
                {
                    Name = spec.Name,
                    Slug = spec.Slug,
                    DisplayOrder = spec.DisplayOrder,
                    ImageUrl = spec.Image,
                    Status = StoreCategoryStatus.Active
                };
                _db.StoreCategories.Add(category);
                categories[spec.Slug] = category;
            }

            for (var i = 0; i < spec.Products.Length; i++)
            {
                var item = spec.Products[i];

                // Already there: whatever an administrator has done to it since stands.
                if (!skus.Add(item.Sku)) continue;

                /*
                 * A starting stock, not a count of anything real.
                 *
                 * The sheet is a price list and says nothing about how many are on hand. Zero would
                 * open the store with every item out of stock, so each starts at a round number to
                 * be corrected from the console's inventory page once the real figures are known.
                 */
                const int stock = 100;

                var product = new StoreProduct
                {
                    Name = item.Name,
                    Slug = item.Slug,
                    Description = item.Description,
                    CategoryId = category.Id,
                    Sku = item.Sku,
                    Price = item.Price,
                    CompareAtPrice = null,
                    Currency = Money.DefaultCurrency,
                    StockQuantity = stock,
                    Status = StoreProductStatus.Active,
                    PaymentMode = StorePaymentMode.Both,
                    IsFeatured = i < 2,
                    RequiresShipping = true,
                    ShippingFee = 0m
                };

                _db.StoreProducts.Add(product);

                _db.StoreProductImages.Add(new StoreProductImage
                {
                    ProductId = product.Id,
                    Url = item.Image,
                    IsPrimary = true,
                    DisplayOrder = 0
                });

                _db.InventoryTransactions.Add(new InventoryTransaction
                {
                    ProductId = product.Id,
                    Type = InventoryTransactionType.Initial,
                    Quantity = stock,
                    BeforeQuantity = 0,
                    AfterQuantity = stock,
                    Reason = "رصيد افتتاحي — صحّح الكمية من صفحة المخزون"
                });

                added++;
            }
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (firstRun || added > 0)
        {
            // The front page caches the store's shelves; without this it would keep showing the
            // old catalogue until the cache expired by itself.
            await _cache.RemoveByPrefixAsync("store:", ct);
        }

        if (firstRun)
        {
            _logger.LogWarning(
                "The WD range replaced the store's catalogue: {Added} products added, {Archived} " +
                "archived and {Hidden} departments hidden. Nothing was deleted.",
                added, archived, hidden);
        }
        else if (added > 0)
        {
            _logger.LogInformation("Added {Added} WD products that were missing.", added);
        }
    }

    /// <summary>
    /// The store's departments in three main groups, in this order:
    /// <list type="number">
    /// <item>مستحضرات تجميل — new; the ADOMO CARE departments become its sub-departments.</item>
    /// <item>كوابل شحن — كيابل الشحن renamed; شواحن جدارية moves under it, beside
    /// شواحن وكوابل السيارة.</item>
    /// <item>سماعات أذن — السماعات renamed.</item>
    /// </list>
    ///
    /// <para>
    /// It runs once, the first time there is no مستحضرات تجميل department. Names are replaced
    /// only while they are still the old ones. After that, the console's changes stand.
    /// </para>
    /// </summary>
    public async Task SeedDepartmentLayoutAsync(CancellationToken ct = default)
    {
        const string cosmeticsSlug = "cosmetics";
        if (await _db.StoreCategories.AnyAsync(c => c.Slug == cosmeticsSlug, ct)) return;

        var all = await _db.StoreCategories.ToListAsync(ct);
        var bySlug = all.ToDictionary(c => c.Slug);
        var now = DateTime.UtcNow;

        var adomo = all.Where(c => c.Slug.StartsWith("adomo-") && c.ParentId is null)
            .OrderBy(c => c.DisplayOrder).ToList();

        var cosmetics = new StoreCategory
        {
            Name = "مستحضرات تجميل",
            Slug = cosmeticsSlug,
            DisplayOrder = 1,
            ImageUrl = adomo.Select(c => c.ImageUrl).FirstOrDefault(u => !string.IsNullOrEmpty(u)),
            Status = StoreCategoryStatus.Active
        };
        _db.StoreCategories.Add(cosmetics);

        // A department that already has sub-departments of its own cannot become one.
        bool CanGoUnder(StoreCategory c) => c.ParentId is null && !all.Any(x => x.ParentId == c.Id);

        foreach (var department in adomo.Where(CanGoUnder))
        {
            department.ParentId = cosmetics.Id;
            department.UpdatedAt = now;
        }

        if (bySlug.TryGetValue("wd-cables", out var cables))
        {
            if (cables.Name == "كيابل الشحن") cables.Name = "كوابل شحن";
            cables.DisplayOrder = 2;
            cables.UpdatedAt = now;

            if (bySlug.TryGetValue("wd-wall-chargers", out var wall) && CanGoUnder(wall))
            {
                wall.ParentId = cables.Id;
                wall.DisplayOrder = 2;
                wall.UpdatedAt = now;
            }
        }

        if (bySlug.TryGetValue("wd-earphones", out var earphones))
        {
            if (earphones.Name == "السماعات") earphones.Name = "سماعات أذن";
            earphones.DisplayOrder = 3;
            earphones.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("store:", ct);
        _logger.LogInformation(
            "Departments regrouped: {Count} ADOMO departments under {Cosmetics}.", adomo.Count, cosmetics.Name);
    }

    /// <summary>
    /// The first sub-department: شواحن وكوابل السيارة, under كيابل الشحن, holding the three car
    /// chargers that had a department of their own (شواحن السيارة), which is hidden once empty.
    ///
    /// <para>
    /// It runs once. When the sub-department already exists nothing is touched — apart from
    /// replacing its first name, "كوابل شحن سيارة" — so whatever an administrator does afterwards
    /// (renaming it, moving products in or out, showing the old department again) stands.
    /// </para>
    /// </summary>
    public async Task SeedCarCablesAsync(CancellationToken ct = default)
    {
        const string slug = "wd-car-cables";
        const string name = "شواحن وكوابل السيارة";

        var existing = await _db.StoreCategories.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (existing is not null)
        {
            // It first went live as "كوابل شحن سيارة"; what is in it are car chargers with their
            // cables. Only that first name is replaced, so a name chosen in the console stands.
            if (existing.Name == "كوابل شحن سيارة")
            {
                existing.Name = name;
                existing.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _cache.RemoveByPrefixAsync("store:", ct);
                _logger.LogInformation("Sub-department {Slug} renamed to {Name}.", slug, name);
            }
            return;
        }

        var main = await _db.StoreCategories.FirstOrDefaultAsync(c => c.Slug == "wd-cables", ct);
        if (main is null || main.ParentId is not null) return;

        var sub = new StoreCategory
        {
            Name = name,
            Slug = slug,
            ParentId = main.Id,
            DisplayOrder = 1,
            ImageUrl = "/catalog/w-x26.webp",
            Status = StoreCategoryStatus.Active
        };
        _db.StoreCategories.Add(sub);

        string[] skus = ["W-X26", "W-X27", "W-X28"];
        var products = await _db.StoreProducts.Where(p => skus.Contains(p.Sku)).ToListAsync(ct);
        var oldIds = products.Select(p => p.CategoryId).Where(id => id is not null).Distinct().ToList();

        foreach (var product in products)
        {
            product.CategoryId = sub.Id;
            product.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        // The department they came from, if nothing is left in it, would open onto an empty page.
        var emptied = await _db.StoreCategories
            .Where(c => oldIds.Contains(c.Id) && c.Id != main.Id &&
                        c.Status == StoreCategoryStatus.Active &&
                        !c.Products.Any(p => p.Status != StoreProductStatus.Archived))
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, StoreCategoryStatus.Hidden)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), ct);

        await _cache.RemoveByPrefixAsync("store:", ct);
        _logger.LogInformation(
            "Sub-department {Name} created under {Main}: {Moved} products moved in, {Hidden} emptied department(s) hidden.",
            sub.Name, main.Name, products.Count, emptied);
    }

    /// <summary>
    /// Puts the ADOMO CARE range in the store beside what is already there. Safe on every start.
    ///
    /// <para>
    /// Unlike the WD range this takes nothing off the shelf: it only adds. Products are matched by
    /// SKU, so a second run adds only what is missing and never undoes a price or a stock level an
    /// administrator has changed since.
    /// </para>
    /// <para>
    /// A product with no price yet goes in hidden — the console can see it and price it, customers
    /// cannot buy something that costs nothing — and a department is shown only once something in
    /// it can be bought. When a later version of the catalogue brings a price for a product still
    /// waiting at zero, that price is applied and the product goes on sale.
    /// </para>
    /// <para>
    /// When a later version reprices or renames a product (<see cref="AdomoCatalog.Previous"/>), the
    /// change is applied only where the product still has exactly what the earlier version seeded:
    /// a price, name or offer an administrator has set since is theirs and stands.
    /// </para>
    /// </summary>
    public async Task SeedAdomoCatalogAsync(
        IReadOnlyList<AdomoCatalog.Product>? products = null,
        IReadOnlyList<AdomoCatalog.Superseded>? previous = null,
        CancellationToken ct = default)
    {
        products ??= AdomoCatalog.Products;
        var superseded = (previous ?? AdomoCatalog.Previous).ToDictionary(p => p.Sku);

        var slugs = AdomoCatalog.Departments.Select(d => d.Slug).ToList();
        var departments = await _db.StoreCategories
            .Where(c => slugs.Contains(c.Slug))
            .ToDictionaryAsync(c => c.Slug, ct);

        foreach (var spec in AdomoCatalog.Departments)
        {
            if (departments.ContainsKey(spec.Slug)) continue;

            var department = new StoreCategory
            {
                Name = spec.Name,
                Slug = spec.Slug,
                DisplayOrder = 10 + spec.DisplayOrder,
                ImageUrl = products.FirstOrDefault(p => p.Department == spec.Slug)?.Image,
                Status = StoreCategoryStatus.Hidden
            };
            _db.StoreCategories.Add(department);
            departments[spec.Slug] = department;
        }

        var skus = products.Select(p => p.Sku).ToList();
        var existing = await _db.StoreProducts
            .Where(p => skus.Contains(p.Sku))
            .ToDictionaryAsync(p => p.Sku, ct);

        int added = 0, priced = 0, updated = 0;

        // Departments where something went on sale in this run. Only these are switched on, so a
        // department an administrator hid on purpose is not brought back by the next deployment.
        var nowSelling = new HashSet<string>();

        foreach (var item in products)
        {
            var onSale = item.Price is > 0;

            if (existing.TryGetValue(item.Sku, out var product))
            {
                // Still waiting for a price, and the catalogue now has one: put it on sale. A
                // product an administrator has already priced or hidden is theirs and left alone.
                if (onSale && product.Price == 0m && product.Status == StoreProductStatus.Hidden)
                {
                    product.Price = item.Price!.Value;
                    product.Status = StoreProductStatus.Active;
                    product.UpdatedAt = DateTime.UtcNow;
                    nowSelling.Add(item.Department);
                    priced++;
                }
                else if (superseded.TryGetValue(item.Sku, out var seeded))
                {
                    var changed = false;

                    // An offer set on it means someone has been pricing it by hand.
                    if (onSale && product.Price == seeded.Price && product.Price != item.Price &&
                        product.CompareAtPrice is null)
                    {
                        product.Price = item.Price!.Value;
                        changed = true;
                    }

                    if (seeded.Name is not null && product.Name == seeded.Name && product.Name != item.Name)
                    {
                        product.Name = item.Name;
                        changed = true;
                    }

                    if (changed)
                    {
                        product.UpdatedAt = DateTime.UtcNow;
                        updated++;
                    }
                }

                continue;
            }

            const int stock = 100;

            product = new StoreProduct
            {
                Name = item.Name,
                Slug = item.Slug,
                Description = item.Description,
                CategoryId = departments[item.Department].Id,
                Sku = item.Sku,
                Price = item.Price ?? 0m,
                Currency = Money.DefaultCurrency,
                StockQuantity = stock,
                Status = onSale ? StoreProductStatus.Active : StoreProductStatus.Hidden,
                PaymentMode = StorePaymentMode.Both,
                RequiresShipping = true,
                ShippingFee = 0m
            };
            _db.StoreProducts.Add(product);

            _db.StoreProductImages.Add(new StoreProductImage
            {
                ProductId = product.Id,
                Url = item.Image,
                IsPrimary = true,
                DisplayOrder = 0
            });

            _db.InventoryTransactions.Add(new InventoryTransaction
            {
                ProductId = product.Id,
                Type = InventoryTransactionType.Initial,
                Quantity = stock,
                BeforeQuantity = 0,
                AfterQuantity = stock,
                Reason = "رصيد افتتاحي — صحّح الكمية من صفحة المخزون"
            });

            if (onSale) nowSelling.Add(item.Department);
            added++;
        }

        // A department appears once something in it can be bought, and not before: an empty tab
        // that opens onto nothing is worse than no tab.
        foreach (var slug in nowSelling)
        {
            if (departments[slug].Status == StoreCategoryStatus.Hidden)
            {
                departments[slug].Status = StoreCategoryStatus.Active;
            }
        }

        await _db.SaveChangesAsync(ct);

        if (added > 0 || priced > 0 || updated > 0)
        {
            await _cache.RemoveByPrefixAsync("store:", ct);
            _logger.LogInformation(
                "ADOMO CARE: {Added} products added, {Priced} put on sale with their new price, {Updated} moved to the current price list.",
                added, priced, updated);
        }
    }

    public async Task SeedDemoStoreAsync(CancellationToken ct = default)
    {
        const string placeholderHost = "https://picsum.photos/";

        var offered = 0;
        var categories = await _db.StoreCategories.ToDictionaryAsync(c => c.Slug, ct);
        var demoProducts = await _db.StoreProducts
            .Where(p => p.Sku.StartsWith("DEMO-"))
            .ToDictionaryAsync(p => p.Sku, ct);

        var skus = (await _db.StoreProducts.Select(p => p.Sku).ToListAsync(ct)).ToHashSet();
        var added = 0;

        foreach (var spec in DemoStoreCatalog.Categories)
        {
            if (!categories.TryGetValue(spec.Slug, out var category))
            {
                category = new StoreCategory
                {
                    Name = spec.Name,
                    Slug = spec.Slug,
                    DisplayOrder = spec.DisplayOrder
                };
                _db.StoreCategories.Add(category);
                categories[spec.Slug] = category;
            }

            if (string.IsNullOrWhiteSpace(category.ImageUrl))
            {
                category.ImageUrl = DemoStoreCatalog.CategoryImage(spec.Image);
            }

            for (var i = 0; i < spec.Products.Length; i++)
            {
                var item = spec.Products[i];

                // Every third demo item carries an offer, so the offers sections have something
                // to show. The price before the offer is what the discount is measured against.
                var discount = i % 3 == 0 ? 15 + i * 5 % 30 : 0;
                var compareAt = discount == 0
                    ? (decimal?)null
                    : Money.Round(item.Price * 100m / (100 - discount));

                if (!skus.Add(item.Sku))
                {
                    // An item seeded before offers existed gains one now; an offer an
                    // administrator set or cleared is left alone.
                    if (demoProducts.TryGetValue(item.Sku, out var seeded) &&
                        seeded.CompareAtPrice is null &&
                        seeded.Price == item.Price &&
                        compareAt is not null)
                    {
                        seeded.CompareAtPrice = compareAt;
                        offered++;
                    }

                    continue;
                }

                // Varied but repeatable stock, so lists do not all show the same number.
                var stock = 10 + (item.Slug.Length * 7 + i * 13) % 50;

                var product = new StoreProduct
                {
                    Name = item.Name,
                    Slug = item.Slug,
                    Description = $"{item.Name} — منتج تجريبي للعرض.",
                    CategoryId = category.Id,
                    Sku = item.Sku,
                    Price = item.Price,
                    CompareAtPrice = compareAt,
                    Currency = Money.DefaultCurrency,
                    StockQuantity = stock,
                    Status = StoreProductStatus.Active,
                    PaymentMode = StorePaymentMode.Both,
                    IsFeatured = i < 2,
                    RequiresShipping = true,
                    ShippingFee = 0m
                };

                _db.StoreProducts.Add(product);

                _db.StoreProductImages.Add(new StoreProductImage
                {
                    ProductId = product.Id,
                    Url = DemoStoreCatalog.ProductImage(item.Image),
                    IsPrimary = true,
                    DisplayOrder = 0
                });

                _db.InventoryTransactions.Add(new InventoryTransaction
                {
                    ProductId = product.Id,
                    Type = InventoryTransactionType.Initial,
                    Quantity = stock,
                    BeforeQuantity = 0,
                    AfterQuantity = stock,
                    Reason = "رصيد افتتاحي"
                });

                added++;
            }
        }

        // The first demo products were seeded with random landscape photos; give them real ones.
        var originalSkus = DemoStoreCatalog.OriginalProductImages.Keys.ToList();
        var placeholders = await _db.StoreProductImages
            .Where(img => img.IsPrimary &&
                          img.Url.StartsWith(placeholderHost) &&
                          img.Product != null &&
                          originalSkus.Contains(img.Product.Sku))
            .Select(img => new { Image = img, img.Product!.Sku })
            .ToListAsync(ct);

        foreach (var row in placeholders)
        {
            row.Image.Url = DemoStoreCatalog.ProductImage(DemoStoreCatalog.OriginalProductImages[row.Sku]);
        }

        await _db.SaveChangesAsync(ct);

        if (added > 0 || placeholders.Count > 0 || offered > 0)
        {
            // Store listings are cached; drop them so the new items show at once, the same way an
            // administrator's catalogue changes do.
            await _cache.RemoveByPrefixAsync("store:", ct);

            _logger.LogInformation(
                "Demo store: added {Added} products, replaced {Replaced} placeholder pictures, " +
                "put {Offered} existing items on offer.",
                added, placeholders.Count, offered);
        }
    }

    private async Task SeedAuctionsAsync(Guid adminId, CancellationToken ct)
    {
        var products = await _db.Products.OrderBy(p => p.Price).ToListAsync(ct);
        if (products.Count < 4) return;

        var main = products.OrderByDescending(p => p.Price).First();
        var prizes = products.Where(p => p.Id != main.Id).OrderByDescending(p => p.Price).Take(3).ToList();

        // A small auction so the whole lifecycle can be exercised without 400 test users. Named
        // after what is being won: that is what a person reads on the front page.
        var auction = BuildAuction(
            await _numbers.NextAuctionNumberAsync(ct), $"مزاد {ProductNames.Rolex}", main, prizes,
            requiredParticipants: 4, startDelaySeconds: 0);

        auction.Status = AuctionStatus.RegistrationOpen;

        _db.Auctions.Add(auction);
        AddPrizes(auction, main, prizes);

        var secondMain = products.First(p => p.Name == ProductNames.Iphone);
        var secondPrizes = products
            .Where(p => p.Name == ProductNames.Galaxy ||
                        p.Name == ProductNames.AppleWatch ||
                        p.Name == ProductNames.Airpods)
            .ToList();

        var second = BuildAuction(
            await _numbers.NextAuctionNumberAsync(ct), $"مزاد {ProductNames.Iphone}",
            secondMain, secondPrizes,
            requiredParticipants: 6, startDelaySeconds: 3600);

        second.Status = AuctionStatus.RegistrationOpen;
        second.IsFeatured = true;

        _db.Auctions.Add(second);
        AddPrizes(second, secondMain, secondPrizes);

        // A third, already running, so the front page has a live poster to show from the first
        // minute a developer opens the app.
        var thirdMain = products.First(p => p.Name == ProductNames.Macbook);
        var thirdPrizes = products
            .Where(p => p.Name == ProductNames.Tablet ||
                        p.Name == ProductNames.Ipad ||
                        p.Name == ProductNames.AppleWatch)
            .ToList();

        var third = BuildAuction(
            await _numbers.NextAuctionNumberAsync(ct), $"مزاد {ProductNames.Macbook}",
            thirdMain, thirdPrizes,
            requiredParticipants: 5, startDelaySeconds: 0);

        third.Status = AuctionStatus.RegistrationOpen;
        third.IsFeatured = true;

        _db.Auctions.Add(third);
        AddPrizes(third, thirdMain, thirdPrizes);

        auction.IsFeatured = true;

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auction.Id,
            UserId = adminId,
            EventType = AuctionEventType.AuctionPublished,
            Timestamp = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
    }

    private static Domain.Entities.Auction BuildAuction(
        string number, string title, Product main, List<Product> prizes,
        int requiredParticipants, int startDelaySeconds)
    {
        var auction = new Domain.Entities.Auction
        {
            AuctionNumber = number,
            Title = title,
            Description = $"اربح {main.Name} وثلاث جوائز أخرى.",
            MainProductId = main.Id,
            MainProductNameSnapshot = main.Name,
            MainImageSnapshot = main.PrimaryImageUrl,
            OriginalPrice = main.Price,
            StartingPercentage = AuctionRules.DefaultStartingPercentage,
            TargetPercentage = AuctionRules.DefaultTargetPercentage,
            FinalPhasePercentage = AuctionRules.DefaultFinalPhasePercentage,
            BidIncrement = AuctionRules.DefaultBidIncrement,
            EntryFee = AuctionRules.DefaultEntryFee,
            Currency = Money.DefaultCurrency,
            RequiredParticipants = requiredParticipants,
            StartDelayAfterParticipantsSeconds = startDelaySeconds,
            DurationSeconds = AuctionRules.DefaultDurationSeconds,
            ExpirationPolicy = ExpirationPolicy.EndAndDistribute
        };

        auction.TargetPrice = AuctionRules.CalculateTargetPrice(auction.OriginalPrice, auction.TargetPercentage);
        auction.FinalPhasePrice = AuctionRules.CalculateFinalPhasePrice(auction.OriginalPrice, auction.FinalPhasePercentage);
        auction.StartingPrice = AuctionRules.CalculateStartingPrice(auction.TargetPrice, auction.StartingPercentage);
        auction.CurrentPrice = auction.StartingPrice;

        return auction;
    }

    private void AddPrizes(Domain.Entities.Auction auction, Product main, List<Product> others)
    {
        _db.AuctionPrizes.Add(new AuctionPrize
        {
            AuctionId = auction.Id,
            ProductId = main.Id,
            Rank = 1,
            OriginalPrice = main.Price,
            ProductNameSnapshot = main.Name,
            ProductImageSnapshot = main.PrimaryImageUrl
        });

        for (var i = 0; i < Math.Min(3, others.Count); i++)
        {
            _db.AuctionPrizes.Add(new AuctionPrize
            {
                AuctionId = auction.Id,
                ProductId = others[i].Id,
                Rank = i + 2,
                OriginalPrice = others[i].Price,
                ProductNameSnapshot = others[i].Name,
                ProductImageSnapshot = others[i].PrimaryImageUrl
            });
        }
    }
}
