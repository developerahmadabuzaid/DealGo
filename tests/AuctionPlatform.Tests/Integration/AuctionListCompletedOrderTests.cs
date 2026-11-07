using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The home page shows the last three auctions to finish, so the finished list goes by when each
/// finished, not by when it was created.
/// </summary>
[Collection("database")]
public class AuctionListCompletedOrderTests
{
    private readonly DatabaseFixture _fixture;

    public AuctionListCompletedOrderTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Finished_auctions_come_latest_finished_first()
    {
        Guid createdFirstFinishedLast, createdLastFinishedFirst;
        var far = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            createdFirstFinishedLast = (await TestData.CreateLiveAuctionAsync(db)).Id;
            createdLastFinishedFirst = (await TestData.CreateLiveAuctionAsync(db)).Id;

            // Far in the future, so no other test's finished auction comes before them.
            await db.Auctions.Where(a => a.Id == createdFirstFinishedLast).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, AuctionStatus.Completed)
                .SetProperty(a => a.CreatedAt, far.AddDays(-10))
                .SetProperty(a => a.FinishedAt, far.AddDays(-1)));
            await db.Auctions.Where(a => a.Id == createdLastFinishedFirst).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, AuctionStatus.Completed)
                .SetProperty(a => a.CreatedAt, far.AddDays(-5))
                .SetProperty(a => a.FinishedAt, far.AddDays(-3)));
        }

        await using var read = _fixture.CreateScope();
        var page = await read.ServiceProvider.GetRequiredService<IAuctionService>()
            .ListAsync(new AuctionListQuery { Group = "completed", PageSize = 2 }, null);

        page.Items.Select(a => a.Id).Should().Equal(createdFirstFinishedLast, createdLastFinishedFirst);
    }
}
