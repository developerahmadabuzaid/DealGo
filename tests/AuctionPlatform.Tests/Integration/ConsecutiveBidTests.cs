using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// No one bids twice in a row: after a bid, someone else has to bid before the same participant
/// may bid again.
/// </summary>
[Collection("database")]
public class ConsecutiveBidTests
{
    private readonly DatabaseFixture _fixture;

    public ConsecutiveBidTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task<(Guid AuctionId, Guid UserA, Guid UserB)> ArrangeAsync()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 1000m, targetPrice: 2500m);
        var a = await TestData.CreateUserAsync(db, "Bidder A", mainBalance: 100m);
        var b = await TestData.CreateUserAsync(db, "Bidder B", mainBalance: 100m);
        await TestData.AddParticipantAsync(db, auction.Id, a.Id);
        await TestData.AddParticipantAsync(db, auction.Id, b.Id);

        return (auction.Id, a.Id, b.Id);
    }

    private async Task<BidResultDto> BidAsync(Guid auctionId, Guid userId, string key)
    {
        await using var scope = _fixture.CreateScope(userId);
        return await scope.ServiceProvider.GetRequiredService<IBidService>()
            .PlaceBidAsync(auctionId, userId, key);
    }

    [Fact]
    public async Task The_last_bidder_cannot_bid_again_until_someone_else_bids()
    {
        var (auctionId, a, b) = await ArrangeAsync();

        await BidAsync(auctionId, a, "a-1");

        var again = () => BidAsync(auctionId, a, "a-2");
        (await again.Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(ErrorCodes.ConsecutiveBidNotAllowed);

        await BidAsync(auctionId, b, "b-1");

        // Someone else has bid, so A may bid again.
        var result = await BidAsync(auctionId, a, "a-3");
        result.NewPrice.Should().Be(1060m);

        await using var verify = _fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.Bids.CountAsync(x => x.AuctionId == auctionId)).Should().Be(3);
    }

    [Fact]
    public async Task Simultaneous_bids_from_one_participant_are_accepted_once()
    {
        var (auctionId, a, _) = await ArrangeAsync();

        var barrier = new TaskCompletionSource();
        var attempts = Enumerable.Range(0, 8).Select(async i =>
        {
            await barrier.Task;
            try
            {
                await BidAsync(auctionId, a, $"burst-{i}");
                return (string?)null;
            }
            catch (DomainException ex)
            {
                return ex.Code;
            }
        }).ToList();

        barrier.SetResult();
        var codes = await Task.WhenAll(attempts);

        codes.Count(c => c is null).Should().Be(1, "the row lock lets the first through and the rest see it");
        codes.Where(c => c is not null).Should().OnlyContain(c =>
            c == ErrorCodes.ConsecutiveBidNotAllowed || c == ErrorCodes.RateLimited);
    }

    [Fact]
    public async Task The_snapshot_says_who_bid_last()
    {
        var (auctionId, a, b) = await ArrangeAsync();

        await BidAsync(auctionId, a, "a-1");

        await using var scope = _fixture.CreateScope(b);
        var auctions = scope.ServiceProvider.GetRequiredService<IAuctionService>();

        var forA = await auctions.GetSnapshotAsync(auctionId, a);
        var forB = await auctions.GetSnapshotAsync(auctionId, b);

        forA.LastBidParticipantId.Should().NotBeNull();
        forA.LastBidParticipantId.Should().Be(forA.MyParticipantId);
        forB.LastBidParticipantId.Should().Be(forA.MyParticipantId);
        forB.MyParticipantId.Should().NotBe(forA.MyParticipantId!.Value);
    }
}
