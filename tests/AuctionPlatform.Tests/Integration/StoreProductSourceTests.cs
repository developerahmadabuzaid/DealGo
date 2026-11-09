using AuctionPlatform.Application.Store;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The dashboard's product list tells products staff added from those imported from M5azn, and
/// can show only one kind or put one kind first.
/// </summary>
[Collection("database")]
public class StoreProductSourceTests
{
    private readonly DatabaseFixture _fixture;

    public StoreProductSourceTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Products_can_be_filtered_and_sorted_by_where_they_came_from()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        Guid admin, imported;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var a = await TestData.CreateStoreProductAsync(db, 10m, 5);
            a.Name = $"Source test {tag} admin";
            var m = await TestData.CreateStoreProductAsync(db, 20m, 5);
            m.Name = $"Source test {tag} imported";
            m.Source = StoreProductSource.M5azn;
            m.M5aznProductId = 9_900_000_000 + Random.Shared.Next(1_000_000);
            await db.SaveChangesAsync();
            (admin, imported) = (a.Id, m.Id);
        }

        await using var read = _fixture.CreateScope();
        var store = read.ServiceProvider.GetRequiredService<IStoreAdminService>();

        var both = await store.GetProductsAsync(new StoreProductQuery { Search = tag, PageSize = 50 });
        both.Items.Should().HaveCount(2);
        both.Items.Single(p => p.Id == admin).Source.Should().Be(StoreProductSource.Admin);
        both.Items.Single(p => p.Id == imported).Source.Should().Be(StoreProductSource.M5azn);

        (await store.GetProductsAsync(new StoreProductQuery { Search = tag, Source = StoreProductSource.M5azn }))
            .Items.Select(p => p.Id).Should().Equal(imported);
        (await store.GetProductsAsync(new StoreProductQuery { Search = tag, Source = StoreProductSource.Admin }))
            .Items.Select(p => p.Id).Should().Equal(admin);

        (await store.GetProductsAsync(new StoreProductQuery { Search = tag, SortBy = "source", SortDescending = true }))
            .Items.Select(p => p.Id).Should().Equal(imported, admin);
        (await store.GetProductsAsync(new StoreProductQuery { Search = tag, SortBy = "source", SortDescending = false }))
            .Items.Select(p => p.Id).Should().Equal(admin, imported);
        (await store.GetProductsAsync(new StoreProductQuery { Search = tag, SortBy = "price", SortDescending = false }))
            .Items.Select(p => p.Id).Should().Equal(admin, imported);
    }

    [Fact]
    public async Task Staff_can_change_their_own_products_stock_but_not_an_imported_ones()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        Guid admin, imported;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var a = await TestData.CreateStoreProductAsync(db, 10m, 5);
            a.Name = $"Stock test {tag} admin";
            var m = await TestData.CreateStoreProductAsync(db, 20m, 5);
            m.Name = $"Stock test {tag} imported";
            m.Source = StoreProductSource.M5azn;
            m.M5aznProductId = 9_900_000_000 + Random.Shared.Next(1_000_000);
            await db.SaveChangesAsync();
            (admin, imported) = (a.Id, m.Id);
        }

        await using var use = _fixture.CreateScope();
        var store = use.ServiceProvider.GetRequiredService<IStoreAdminService>();

        (await store.AdjustInventoryAsync(admin, new AdjustInventoryRequest(3, "New shipment", null)))
            .AfterQuantity.Should().Be(8);

        var refuse = () => store.AdjustInventoryAsync(imported, new AdjustInventoryRequest(3, "New shipment", null));
        await refuse.Should().ThrowAsync<DomainException>();

        var rows = await store.GetInventoryAsync(new StoreProductQuery { Search = tag });
        var row = rows.Items.Single(r => r.ProductId == imported);
        row.Source.Should().Be(StoreProductSource.M5azn);
        row.StockQuantity.Should().Be(5);
    }
}
