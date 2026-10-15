using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// A database of its own for the WD catalogue.
///
/// The seeder's first run takes every other product off the shelf. In the shared database that
/// would archive the products other tests depend on and fail them in whatever order they happened
/// to run — so this collection gets its own container, and nothing else can see what it does.
/// </summary>
[CollectionDefinition("wd-catalog")]
public sealed class WdCatalogCollection : ICollectionFixture<DatabaseFixture>;

/// <summary>
/// The WD range going into the store.
///
/// This runs against the live database on every start, so the things that matter are the ones
/// that would be expensive to get wrong there: it must not duplicate, must not undo an
/// administrator's changes, must not delete anything, and — above all — must replace the old
/// shelves exactly once rather than hiding every new product an administrator adds afterwards.
///
/// The steps run in one test, in order, because each depends on the state the last one left.
/// </summary>
[Collection("wd-catalog")]
public class WdCatalogSeedTests
{
    private readonly DatabaseFixture _fixture;

    public WdCatalogSeedTests(DatabaseFixture fixture) => _fixture = fixture;

    private static int WdProductCount => WdCatalog.Categories.Sum(c => c.Products.Length);

    [Fact]
    public async Task The_range_replaces_the_old_shelves_once_and_then_leaves_the_store_alone()
    {
        // ------------------------------------------------------------ a store that already trades
        Guid oldProduct, oldCategory;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var category = new StoreCategory
            {
                Name = "إلكترونيات",
                Slug = "electronics",
                Status = StoreCategoryStatus.Active
            };
            db.StoreCategories.Add(category);

            var product = new StoreProduct
            {
                Name = "هاتف تجريبي",
                Slug = "demo-phone",
                Sku = "DEMO-PHONE",
                Price = 1000m,
                StockQuantity = 5,
                Status = StoreProductStatus.Active,
                CategoryId = category.Id
            };
            db.StoreProducts.Add(product);

            await db.SaveChangesAsync();
            oldProduct = product.Id;
            oldCategory = category.Id;
        }

        // ------------------------------------------------------------ the first run
        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedWdCatalogAsync();
        }

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var wd = await db.StoreProducts.AsNoTracking()
                .Where(p => p.Status == StoreProductStatus.Active)
                .ToListAsync();

            wd.Should().HaveCount(WdProductCount, "every item in the range is on sale");
            wd.Should().OnlyContain(p => WdCatalog.IsWdSku(p.Sku), "only the range is on sale");

            // Off the shelf, but still in the database — an order for it must still resolve.
            var old = await db.StoreProducts.AsNoTracking().FirstAsync(p => p.Id == oldProduct);
            old.Status.Should().Be(StoreProductStatus.Archived);

            // Its department would otherwise draw a tab that opens onto nothing.
            (await db.StoreCategories.AsNoTracking().FirstAsync(c => c.Id == oldCategory))
                .Status.Should().Be(StoreCategoryStatus.Hidden);

            (await db.StoreCategories.AsNoTracking()
                    .CountAsync(c => c.Slug.StartsWith("wd-") && c.Status == StoreCategoryStatus.Active))
                .Should().Be(WdCatalog.Categories.Count);
        }

        // ------------------------------------------------------------ the store keeps trading
        Guid newProduct;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // An administrator adds a product of their own through the console...
            var added = new StoreProduct
            {
                Name = "منتج أضافه المشرف",
                Slug = "admin-added",
                Sku = "ADMIN-001",
                Price = 50m,
                StockQuantity = 3,
                Status = StoreProductStatus.Active
            };
            db.StoreProducts.Add(added);

            // ...and changes the price of one of the range.
            var cable = await db.StoreProducts.FirstAsync(p => p.Sku == "W-X10");
            cable.Price = 9.99m;

            await db.SaveChangesAsync();
            newProduct = added.Id;
        }

        // ------------------------------------------------------------ the next deployment
        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedWdCatalogAsync();
        }

        await using (var verify = _fixture.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // The one this whole design exists for: a product added after the first run survives
            // every run after it. Hiding "everything that is not WD" on each start would have
            // archived it here.
            (await db.StoreProducts.AsNoTracking().FirstAsync(p => p.Id == newProduct))
                .Status.Should().Be(StoreProductStatus.Active,
                    "a product an administrator added must not be hidden by the next deployment");

            // Nothing doubled.
            // Read the SKUs first: IsWdSku is ordinary C#, and cannot be turned into SQL.
            (await db.StoreProducts.AsNoTracking().Select(p => p.Sku).ToListAsync())
                .Count(WdCatalog.IsWdSku)
                .Should().Be(WdProductCount);

            // Their price stands; the sheet does not overwrite it.
            (await db.StoreProducts.AsNoTracking().FirstAsync(p => p.Sku == "W-X10"))
                .Price.Should().Be(9.99m);

            // And still nothing was deleted.
            (await db.StoreProducts.AsNoTracking().AnyAsync(p => p.Id == oldProduct))
                .Should().BeTrue();
        }
    }

    [Fact]
    public void The_range_is_priced_from_the_sheet_and_every_item_has_a_picture()
    {
        var all = WdCatalog.Categories.SelectMany(c => c.Products).ToList();

        all.Should().HaveCount(19);
        all.Select(p => p.Sku).Should().OnlyHaveUniqueItems();
        all.Should().OnlyContain(p => p.Price > 0);
        all.Should().OnlyContain(p => p.Image.StartsWith("/catalog/"));

        // Spot checks against the sheet's retail column — not its cost column, which is 15% lower.
        all.Single(p => p.Sku == "W-X10").Price.Should().Be(12.50m);
        all.Single(p => p.Sku == "W-X18").Price.Should().Be(18.50m);
        all.Single(p => p.Sku == "W-X25").Price.Should().Be(52.00m);
        all.Single(p => p.Sku == "W-X28").Price.Should().Be(34.00m);
        all.Single(p => p.Sku == "WD-E2").Price.Should().Be(18.50m);
    }

    [Fact]
    public void Every_picture_the_range_names_is_actually_shipped()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "AuctionPlatform.Api", "wwwroot"));

        var missing = WdCatalog.Categories
            .SelectMany(c => c.Products)
            .Select(p => p.Image)
            .Where(image => !File.Exists(Path.Combine(root, image.TrimStart('/'))))
            .ToList();

        missing.Should().BeEmpty("a product whose picture is not shipped shows a grey box in the store");
    }
}
