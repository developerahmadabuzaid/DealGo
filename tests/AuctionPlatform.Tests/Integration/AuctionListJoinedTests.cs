using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The app marks an upcoming auction the user has already joined as "joined" rather than
/// "upcoming", so the list has to say, per auction, whether the caller is in it.
/// </summary>
[Collection("database")]
public class AuctionListJoinedTests
{
    private readonly DatabaseFixture _fixture;

    public AuctionListJoinedTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Each_auction_says_whether_the_caller_has_joined_it()
    {
        Guid joined, other, member, stranger;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            member = (await TestData.CreateUserAsync(db, "Member")).Id;
            stranger = (await TestData.CreateUserAsync(db, "Stranger")).Id;

            joined = (await TestData.CreateLiveAuctionAsync(db)).Id;
            other = (await TestData.CreateLiveAuctionAsync(db)).Id;

            await TestData.AddParticipantAsync(db, joined, member);

            // Both waiting for participants, which is what the "upcoming" list shows.
            await db.Auctions.Where(a => a.Id == joined || a.Id == other)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AuctionStatus.WaitingForParticipants));
        }

        await using var read = _fixture.CreateScope();
        var auctions = read.ServiceProvider.GetRequiredService<IAuctionService>();
        var query = new AuctionListQuery { Group = "upcoming", PageSize = 100 };

        var forMember = (await auctions.ListAsync(query, member)).Items;
        forMember.Single(a => a.Id == joined).HasJoined.Should().BeTrue();
        forMember.Single(a => a.Id == other).HasJoined.Should().BeFalse();

        (await auctions.ListAsync(query, stranger)).Items
            .Single(a => a.Id == joined).HasJoined.Should().BeFalse();

        (await auctions.ListAsync(query, null)).Items
            .Single(a => a.Id == joined).HasJoined.Should().BeFalse("an anonymous caller has joined nothing");
    }
}
