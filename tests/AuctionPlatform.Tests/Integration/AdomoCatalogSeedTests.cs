using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

[CollectionDefinition("adomo-catalog")]
public sealed class AdomoCatalogCollection : ICollectionFixture<DatabaseFixture>;

/// <summary>
/// The ADOMO CARE range going into the store beside what is already there.
///
/// It runs against the live database on every start, so it must add without duplicating, must not
/// undo an administrator's changes, must not take anything else off the shelf, and must keep a
/// product without a price out of customers' sight until it has one.
/// </summary>
[Collection("adomo-catalog")]
public class AdomoCatalogSeedTests
{
    private readonly DatabaseFixture _fixture;

    public AdomoCatalogSeedTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_range_goes_on_sale_beside_the_store_and_then_leaves_it_alone()
    {
        // A store already selling something of its own.
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.StoreProducts.Add(new StoreProduct
            {
                Name = "منتج قائم", Slug = "existing", Sku = "W-X10", Price = 12.5m,
                StockQuantity = 5, Status = StoreProductStatus.Active
            });
            await db.SaveChangesAsync();
        }

        await Seed();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // The catalogue's own SKUs: the other test in this collection adds one of its own.
            var catalogSkus = AdomoCatalog.Products.Select(p => p.Sku).ToList();
            var adomo = await db.StoreProducts.AsNoTracking()
                .Where(p => catalogSkus.Contains(p.Sku)).ToListAsync();

            adomo.Should().HaveCount(AdomoCatalog.Products.Count);
            adomo.Should().OnlyContain(p => p.Status == StoreProductStatus.Active && p.Price > 0);

            (await db.StoreCategories.AsNoTracking()
                    .CountAsync(c => c.Slug.StartsWith("adomo-") && c.Status == StoreCategoryStatus.Active))
                .Should().Be(AdomoCatalog.Departments.Count);

            // Beside, not instead of.
            (await db.StoreProducts.AsNoTracking().FirstAsync(p => p.Sku == "W-X10"))
                .Status.Should().Be(StoreProductStatus.Active);
        }

        // An administrator reprices one product and hides one department.
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.StoreProducts.FirstAsync(p => p.Sku == "AD-01")).Price = 79m;
            (await db.StoreCategories.FirstAsync(c => c.Slug == "adomo-hair")).Status = StoreCategoryStatus.Hidden;
            await db.SaveChangesAsync();
        }

        // The next deployment.
        await Seed();

        await using (var verify = _fixture.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var catalogSkus = AdomoCatalog.Products.Select(p => p.Sku).ToList();
            (await db.StoreProducts.AsNoTracking().CountAsync(p => catalogSkus.Contains(p.Sku)))
                .Should().Be(AdomoCatalog.Products.Count, "nothing is added twice");

            (await db.StoreProducts.AsNoTracking().FirstAsync(p => p.Sku == "AD-01"))
                .Price.Should().Be(79m, "the administrator's price stands");

            (await db.StoreCategories.AsNoTracking().FirstAsync(c => c.Slug == "adomo-hair"))
                .Status.Should().Be(StoreCategoryStatus.Hidden, "a department hidden on purpose stays hidden");
        }
    }

    [Fact]
    public async Task A_product_without_a_price_waits_hidden_and_goes_on_sale_once_it_has_one()
    {
        var waiting = new AdomoCatalog.Product(
            "AD-T1", "adomo-more", "منتج بلا سعر", null, "/catalog/adomo-p17.webp", "وصف");

        await Seed(new[] { waiting });

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var product = await db.StoreProducts.AsNoTracking().FirstAsync(p => p.Sku == "AD-T1");

            product.Status.Should().Be(StoreProductStatus.Hidden, "customers cannot buy something that costs nothing");
            product.Price.Should().Be(0m);
        }

        // A later catalogue brings the price.
        await Seed(new[] { waiting with { Price = 29m } });

        await using (var verify = _fixture.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var product = await db.StoreProducts.AsNoTracking().FirstAsync(p => p.Sku == "AD-T1");

            product.Status.Should().Be(StoreProductStatus.Active);
            product.Price.Should().Be(29m);
        }
    }

    [Fact]
    public async Task A_new_price_list_replaces_what_was_seeded_but_not_what_an_administrator_set()
    {
        var untouched = new AdomoCatalog.Product(
            "AD-T2", "adomo-more", "منتج قديم", 40m, "/catalog/adomo-p17.webp", "وصف");
        var repricedByHand = new AdomoCatalog.Product(
            "AD-T3", "adomo-more", "منتج آخر", 40m, "/catalog/adomo-p17.webp", "وصف");

        await Seed(new[] { untouched, repricedByHand });

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.StoreProducts.FirstAsync(p => p.Sku == "AD-T3")).Price = 45m;
            await db.SaveChangesAsync();
        }

        // The next version of the catalogue, carrying the brand's price list.
        var previous = new AdomoCatalog.Superseded[] { new("AD-T2", 40m, "منتج قديم"), new("AD-T3", 40m) };
        await Seed(
            new[] { untouched with { Price = 34.5m, Name = "منتج باسمه الجديد" }, repricedByHand with { Price = 34.5m } },
            previous);

        await using (var verify = _fixture.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var moved = await db.StoreProducts.AsNoTracking().FirstAsync(p => p.Sku == "AD-T2");
            moved.Price.Should().Be(34.5m);
            moved.Name.Should().Be("منتج باسمه الجديد");

            (await db.StoreProducts.AsNoTracking().FirstAsync(p => p.Sku == "AD-T3"))
                .Price.Should().Be(45m, "the administrator's price stands");
        }
    }

    [Fact]
    public void Every_product_is_priced_and_every_picture_is_shipped()
    {
        AdomoCatalog.Products.Should().HaveCount(25);
        AdomoCatalog.Products.Select(p => p.Sku).Should().OnlyHaveUniqueItems();
        AdomoCatalog.Products.Should().OnlyContain(p => p.Price > 0);
        AdomoCatalog.Products.Should().OnlyContain(p =>
            AdomoCatalog.Departments.Any(d => d.Slug == p.Department));

        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "AuctionPlatform.Api", "wwwroot"));

        AdomoCatalog.Products
            .Where(p => !File.Exists(Path.Combine(root, p.Image.TrimStart('/'))))
            .Select(p => p.Sku)
            .Should().BeEmpty("a product whose picture is not shipped shows a grey box");

        // Every superseded entry names a product still in the catalogue, at a different price.
        AdomoCatalog.Previous.Select(p => p.Sku).Should().OnlyHaveUniqueItems();
        AdomoCatalog.Previous.Should().OnlyContain(old =>
            AdomoCatalog.Products.Any(p => p.Sku == old.Sku && p.Price != old.Price));
    }

    [Fact]
    public void Prices_follow_the_brand_price_list()
    {
        // October 2026 list, VAT included. Spot checks across every department.
        Price("AD-11").Should().Be(77m);
        Price("AD-07").Should().Be(60m);
        Price("AD-13").Should().Be(17.25m);
        Price("AD-17").Should().Be(3.45m);
        Price("AD-19").Should().Be(28.75m);
        Price("AD-23").Should().Be(34.5m);
        Price("AD-24").Should().Be(9.20m);
        Price("AD-25").Should().Be(34.5m);

        // Not on that list: left at what they were.
        Price("AD-14").Should().Be(25m);
        Price("AD-15").Should().Be(35m);
        Price("AD-16").Should().Be(35m);

        static decimal? Price(string sku) => AdomoCatalog.Products.Single(p => p.Sku == sku).Price;
    }

    private async Task Seed(
        IReadOnlyList<AdomoCatalog.Product>? products = null,
        IReadOnlyList<AdomoCatalog.Superseded>? previous = null)
    {
        await using var scope = _fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAdomoCatalogAsync(products, previous);
    }
}
