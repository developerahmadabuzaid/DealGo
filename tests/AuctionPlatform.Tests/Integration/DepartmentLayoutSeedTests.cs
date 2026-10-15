using AuctionPlatform.Application.Store;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

[CollectionDefinition("department-layout")]
public sealed class DepartmentLayoutCollection : ICollectionFixture<DatabaseFixture>;

/// <summary>
/// The store regrouped into three main departments — مستحضرات تجميل، كوابل شحن، سماعات أذن —
/// once, on the live store at start-up, in the same order the server runs its steps.
/// </summary>
[Collection("department-layout")]
public class DepartmentLayoutSeedTests
{
    private readonly DatabaseFixture _fixture;

    public DepartmentLayoutSeedTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task StartAsync()
    {
        await using var scope = _fixture.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedWdCatalogAsync();
        await seeder.SeedCarCablesAsync();
        await seeder.SeedAdomoCatalogAsync();
        await seeder.SeedDepartmentLayoutAsync();
    }

    [Fact]
    public async Task Three_main_departments_in_order_with_the_rest_under_them_once()
    {
        await StartAsync();

        await using (var scope = _fixture.CreateScope())
        {
            var categories = await scope.ServiceProvider.GetRequiredService<IStoreCatalogService>()
                .GetCategoriesAsync();

            categories.Where(c => c.ParentId == null).Select(c => c.Name)
                .Should().Equal("مستحضرات تجميل", "كوابل شحن", "سماعات أذن");

            var cosmetics = categories.Single(c => c.Name == "مستحضرات تجميل");
            var cables = categories.Single(c => c.Name == "كوابل شحن");

            categories.Where(c => c.ParentId == cosmetics.Id).Select(c => c.Name)
                .Should().BeEquivalentTo(AdomoCatalog.Departments.Select(d => d.Name));
            cosmetics.ProductCount.Should().Be(AdomoCatalog.Products.Count);

            categories.Where(c => c.ParentId == cables.Id).Select(c => c.Name)
                .Should().Equal("شواحن وكوابل السيارة", "شواحن جدارية");
        }

        // An administrator renames a main department.
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.StoreCategories.SingleAsync(c => c.Slug == "wd-earphones")).Name = "سماعات";
            await db.SaveChangesAsync();
        }

        // The next start changes nothing.
        await StartAsync();

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db2.StoreCategories.CountAsync(c => c.Slug == "cosmetics")).Should().Be(1);
        (await db2.StoreCategories.AsNoTracking().SingleAsync(c => c.Slug == "wd-earphones"))
            .Name.Should().Be("سماعات");
    }
}
