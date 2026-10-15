using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The demo store is seeded on every start of a development server, so running it again must
/// change nothing.
/// </summary>
[Collection("database")]
public class DemoStoreSeedTests
{
    private readonly DatabaseFixture _fixture;

    public DemoStoreSeedTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task SeedAsync()
    {
        await using var scope = _fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedDemoStoreAsync();
    }

    [Fact]
    public async Task Every_demo_category_has_a_picture_and_enough_products_to_fill_the_grid()
    {
        await SeedAsync();

        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var categories = await db.StoreCategories
            .Where(c => c.Slug == "kitchen" || c.Slug == "watches" || c.Slug == "groceries")
            .Select(c => new { c.Slug, c.ImageUrl, Products = c.Products.Count })
            .ToListAsync();

        categories.Should().HaveCount(3);
        categories.Should().OnlyContain(c => c.ImageUrl != null && c.ImageUrl.StartsWith("https://"));
        categories.Should().OnlyContain(c => c.Products >= 8);

        var demoWithoutPicture = await db.StoreProducts
            .Where(p => p.Sku.StartsWith("DEMO-") && !db.StoreProductImages.Any(i => i.ProductId == p.Id))
            .CountAsync();
        demoWithoutPicture.Should().Be(0);
    }

    [Fact]
    public async Task Seeding_again_adds_nothing()
    {
        await SeedAsync();

        async Task<(int Categories, int Products, int Images)> CountAsync()
        {
            await using var scope = _fixture.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return (await db.StoreCategories.CountAsync(),
                    await db.StoreProducts.CountAsync(),
                    await db.StoreProductImages.CountAsync());
        }

        var before = await CountAsync();
        await SeedAsync();
        var after = await CountAsync();

        after.Should().Be(before);
    }
}
