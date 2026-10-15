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
    public void Every_product_is_priced_and_every_picture_is_shipped()
    {
        AdomoCatalog.Products.Should().HaveCount(23);
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
    }

    private async Task Seed(IReadOnlyList<AdomoCatalog.Product>? products = null)
    {
        await using var scope = _fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAdomoCatalogAsync(products);
    }
}
