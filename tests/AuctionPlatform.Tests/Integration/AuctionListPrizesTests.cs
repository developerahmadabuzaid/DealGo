using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The app shows each auction's four devices under it in every list, so the list carries them,
/// in place order, rather than the app asking for each auction's page.
/// </summary>
[Collection("database")]
public class AuctionListPrizesTests
{
    private readonly DatabaseFixture _fixture;

    public AuctionListPrizesTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Each_listed_auction_carries_its_devices_in_place_order()
    {
        Guid id;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            id = (await TestData.CreateLiveAuctionAsync(db)).Id;
        }

        await using var read = _fixture.CreateScope();
        var list = await read.ServiceProvider.GetRequiredService<IAuctionService>()
            .ListAsync(new AuctionListQuery { Group = "live", PageSize = 100 }, null);

        var prizes = list.Items.Single(a => a.Id == id).Prizes;
        prizes.Should().NotBeNull();
        prizes!.Select(p => p.Rank).Should().Equal(1, 2, 3, 4);
        prizes.Select(p => p.Name).Should().Equal("Test Prize", "Prize 2", "Prize 3", "Prize 4");
        prizes[0].OriginalPrice.Should().Be(5000m);
    }
}
