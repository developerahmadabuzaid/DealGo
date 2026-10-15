using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

[CollectionDefinition("car-cables")]
public sealed class CarCablesCollection : ICollectionFixture<DatabaseFixture>;

/// <summary>
/// The first sub-department, made on the live store at start-up: شواحن وكوابل السيارة under
/// كيابل الشحن, with the three car chargers moved into it. It must happen once and then leave
/// the administrator's changes alone. The steps run in one test, in order, because each depends on
/// the state the last one left.
/// </summary>
[Collection("car-cables")]
public class CarCablesSeedTests
{
    private readonly DatabaseFixture _fixture;

    public CarCablesSeedTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task RunAsync()
    {
        await using var scope = _fixture.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedWdCatalogAsync();
        await seeder.SeedCarCablesAsync();
    }

    [Fact]
    public async Task The_car_chargers_move_into_the_new_sub_department_once_and_it_is_renamed_once()
    {
        await RunAsync();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var main = await db.StoreCategories.AsNoTracking().SingleAsync(c => c.Slug == "wd-cables");
            var sub = await db.StoreCategories.AsNoTracking().SingleAsync(c => c.Slug == "wd-car-cables");

            sub.Name.Should().Be("شواحن وكوابل السيارة");
            sub.ParentId.Should().Be(main.Id);
            sub.Status.Should().Be(StoreCategoryStatus.Active);

            (await db.StoreProducts.AsNoTracking()
                    .Where(p => p.CategoryId == sub.Id).Select(p => p.Sku).ToListAsync())
                .Should().BeEquivalentTo("W-X26", "W-X27", "W-X28");

            (await db.StoreCategories.AsNoTracking().SingleAsync(c => c.Slug == "wd-car-chargers"))
                .Status.Should().Be(StoreCategoryStatus.Hidden, "nothing is left in it");

            // The cables themselves stay in the main department.
            (await db.StoreProducts.AsNoTracking().CountAsync(p => p.CategoryId == main.Id))
                .Should().BeGreaterThan(0);
        }

        // An administrator moves one charger back and shows the old department again.
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var old = await db.StoreCategories.SingleAsync(c => c.Slug == "wd-car-chargers");
            old.Status = StoreCategoryStatus.Active;
            (await db.StoreProducts.SingleAsync(p => p.Sku == "W-X28")).CategoryId = old.Id;
            await db.SaveChangesAsync();
        }

        // The next start.
        await RunAsync();

        await using (var verify = _fixture.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.StoreCategories.CountAsync(c => c.Slug == "wd-car-cables")).Should().Be(1);

            var old = await db.StoreCategories.AsNoTracking().SingleAsync(c => c.Slug == "wd-car-chargers");
            old.Status.Should().Be(StoreCategoryStatus.Active, "the administrator's choice stands");
            (await db.StoreProducts.AsNoTracking().SingleAsync(p => p.Sku == "W-X28"))
                .CategoryId.Should().Be(old.Id);
        }

        // The name it first went live with (v13) is replaced; a name chosen later stands.
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.StoreCategories.SingleAsync(c => c.Slug == "wd-car-cables")).Name = "كوابل شحن سيارة";
            await db.SaveChangesAsync();
        }

        await RunAsync();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sub = await db.StoreCategories.SingleAsync(c => c.Slug == "wd-car-cables");
            sub.Name.Should().Be("شواحن وكوابل السيارة");

            // Then an administrator picks a name of their own.
            sub.Name = "اسم من اللوحة";
            await db.SaveChangesAsync();
        }

        await RunAsync();

        await using var last = _fixture.CreateScope();
        (await last.ServiceProvider.GetRequiredService<ApplicationDbContext>().StoreCategories
                .AsNoTracking().SingleAsync(c => c.Slug == "wd-car-cables"))
            .Name.Should().Be("اسم من اللوحة");
    }
}
