using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// Bids and joins take turns per auction in the server's own queue before the database. The
/// database lock still decides; these tests cover what the queue adds: announcements in order,
/// refusing a closed auction without a transaction, and nothing left behind by a bidder who gave up.
/// </summary>
[Collection("database")]
public class AuctionQueueTests
{
    private readonly DatabaseFixture _fixture;

    public AuctionQueueTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task<(Guid AuctionId, List<Guid> Users)> LiveAuctionAsync(
        int bidders, decimal current = 1000m, decimal target = 2500m, Action<Domain.Entities.Auction>? change = null)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: current, targetPrice: target);
        change?.Invoke(auction);
        await db.SaveChangesAsync();

        var users = new List<Guid>();
        for (var i = 0; i < bidders; i++)
        {
            var user = await TestData.CreateUserAsync(db, $"Queue {i}");
            await TestData.AddParticipantAsync(db, auction.Id, user.Id);
            users.Add(user.Id);
        }
        return (auction.Id, users);
    }

    private async Task<BidResultDto> BidAsync(Guid auctionId, Guid userId, string key, CancellationToken ct = default)
    {
        await using var scope = _fixture.CreateScope(userId);
        return await scope.ServiceProvider.GetRequiredService<IBidService>().PlaceBidAsync(auctionId, userId, key, ct);
    }

    private async Task<int> StoredBidsAsync(Guid auctionId)
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Bids.CountAsync(b => b.AuctionId == auctionId);
    }

    [Fact]
    public async Task Bids_placed_at_once_are_each_announced_with_their_number()
    {
        var (id, users) = await LiveAuctionAsync(20);

        var results = await Task.WhenAll(users.Select((u, i) => BidAsync(id, u, $"order-{i}")));

        results.Select(r => r.SequenceNumber).Should().BeEquivalentTo(Enumerable.Range(1, 20).Select(i => (long)i));

        // Every bid is announced once, and every price update carries the number of the bid that set
        // it — the app keeps the highest number, so the order they travel in no longer matters.
        _fixture.Realtime.BidsAnnounced.Where(b => b.AuctionId == id).Select(b => b.Sequence)
            .Should().BeEquivalentTo(Enumerable.Range(1, 20).Select(i => (long)i));
        var prices = _fixture.Realtime.PricesAnnounced.Where(p => p.AuctionId == id).ToList();
        prices.Select(p => p.Sequence).Should().BeEquivalentTo(Enumerable.Range(1, 20).Select(i => (long)i));
        prices.OrderBy(p => p.Sequence).Select(p => p.Price).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task A_duplicate_that_waited_behind_its_original_gets_the_original_back()
    {
        var (id, users) = await LiveAuctionAsync(1);

        // Both copies get past the replay check before either is placed, then wait their turn.
        var busy = await _fixture.Queue.EnterAsync(id, "test", CancellationToken.None);
        var first = BidAsync(id, users[0], "same-key");
        var second = BidAsync(id, users[0], "same-key");
        await Task.Delay(200);
        busy.Dispose();

        var results = await Task.WhenAll(first, second);

        results.Select(r => r.BidId).Distinct().Should().ContainSingle("the same request sent twice is one bid");
        results.Count(r => r.WasDuplicate).Should().Be(1);
        (await StoredBidsAsync(id)).Should().Be(1);
    }

    [Fact]
    public async Task A_bidder_changed_while_waiting_is_read_again_and_counted_right()
    {
        var (id, users) = await LiveAuctionAsync(1);

        // The bid reads its participant row, then waits for the auction's turn.
        var busy = await _fixture.Queue.EnterAsync(id, "test", CancellationToken.None);
        var waiting = BidAsync(id, users[0], "stale-0");
        await Task.Delay(200);

        // Meanwhile the row changes, as another of this person's devices bidding would change it.
        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AuctionParticipants
                .Where(p => p.AuctionId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.BidCount, 5));
        }
        busy.Dispose();

        (await waiting).SequenceNumber.Should().Be(1);

        await using var verify = _fixture.CreateScope();
        var participant = await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .AuctionParticipants.AsNoTracking().SingleAsync(p => p.AuctionId == id);
        participant.BidCount.Should().Be(6, "the stale copy was not written back; the bid was counted on the fresh one");
        (await StoredBidsAsync(id)).Should().Be(1);
    }

    [Fact]
    public async Task The_auction_row_knows_who_bid_last()
    {
        var (id, users) = await LiveAuctionAsync(2);

        await BidAsync(id, users[0], "last-0");
        await BidAsync(id, users[1], "last-1");

        await using var scope = _fixture.CreateScope(users[0]);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var second = await db.AuctionParticipants.AsNoTracking().SingleAsync(p => p.AuctionId == id && p.UserId == users[1]);

        (await db.Auctions.AsNoTracking().SingleAsync(a => a.Id == id)).LastBidParticipantId.Should().Be(second.Id);
        (await scope.ServiceProvider.GetRequiredService<IAuctionService>().GetSnapshotAsync(id, users[0]))
            .LastBidParticipantId.Should().Be(second.Id);

        // And the rule reads it: the second bidder cannot go again straight away.
        var twice = () => BidAsync(id, users[1], "last-2");
        (await twice.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ConsecutiveBidNotAllowed);
    }

    [Fact]
    public async Task Once_the_target_is_reached_later_bids_are_refused_without_a_transaction()
    {
        var (id, users) = await LiveAuctionAsync(2, current: 2480m, target: 2500m);

        (await BidAsync(id, users[0], "target-0")).ReachedTarget.Should().BeTrue();
        _fixture.Queue.RefusalFor(id).Should().NotBeNull();

        // Proof the refusal never asked the database: even with the row made biddable again behind
        // the server's back, the late bid is refused, with the answer the database would have given.
        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Auctions
                .Where(a => a.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, AuctionStatus.Live)
                    .SetProperty(a => a.TargetReached, false)
                    .SetProperty(a => a.TargetPrice, 5000m));
        }

        var late = () => BidAsync(id, users[1], "target-1");
        (await late.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AuctionFinished);
        (await StoredBidsAsync(id)).Should().Be(1);
    }

    [Fact]
    public async Task Running_out_of_time_closes_for_good_only_when_the_auction_cannot_be_extended()
    {
        // Ends and distributes: once its time is up, it can never take another bid.
        var (ending, endingUsers) = await LiveAuctionAsync(1, change: a => a.ActualStartedAt = DateTime.UtcNow.AddHours(-2));
        var tooLate = () => BidAsync(ending, endingUsers[0], "end-0");
        (await tooLate.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AuctionFinished);
        _fixture.Queue.RefusalFor(ending).Should().NotBeNull();

        // Extends: the same moment is not the end, so it must stay open for the time it is given.
        var (extending, extendingUsers) = await LiveAuctionAsync(1, change: a =>
        {
            a.ActualStartedAt = DateTime.UtcNow.AddHours(-2);
            a.ExpirationPolicy = ExpirationPolicy.Extend;
        });
        var notYet = () => BidAsync(extending, extendingUsers[0], "extend-0");
        (await notYet.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AuctionFinished);
        _fixture.Queue.RefusalFor(extending).Should().BeNull("the expiry worker is about to give it more time");

        // What the expiry worker does under that policy.
        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Auctions
                .Where(a => a.Id == extending)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.DurationSeconds, a => a.DurationSeconds + 7200));
        }

        (await BidAsync(extending, extendingUsers[0], "extend-1")).SequenceNumber.Should().Be(1);
    }

    [Fact]
    public async Task A_bidder_who_gives_up_while_waiting_never_bids()
    {
        var (id, users) = await LiveAuctionAsync(1);
        using var cancel = new CancellationTokenSource();

        // Somebody else's bid has the auction's turn.
        var busy = await _fixture.Queue.EnterAsync(id, "test", CancellationToken.None);
        var waiting = BidAsync(id, users[0], "quit-0", cancel.Token);
        await Task.Delay(200);

        cancel.Cancel();
        var gaveUp = async () => await waiting;
        await gaveUp.Should().ThrowAsync<OperationCanceledException>();
        busy.Dispose();

        (await StoredBidsAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task People_joining_at_once_are_each_counted_and_charged_once()
    {
        const int people = 30;
        Guid auctionId;
        var users = new List<Guid>();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db, requiredParticipants: people);
            auction.Status = AuctionStatus.RegistrationOpen;
            auction.ActualStartedAt = null;
            auction.StartDelayAfterParticipantsSeconds = 60;
            await db.SaveChangesAsync();
            auctionId = auction.Id;

            for (var i = 0; i < people; i++)
                users.Add((await TestData.CreateUserAsync(db, $"Joiner {i}", mainBalance: 20m)).Id);
        }

        await Task.WhenAll(users.Select(async u =>
        {
            await using var scope = _fixture.CreateScope(u);
            await scope.ServiceProvider.GetRequiredService<IParticipationService>().JoinAsync(auctionId, u, $"join-{u}");
        }));

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db2.Auctions.AsNoTracking().SingleAsync(a => a.Id == auctionId);

        stored.ParticipantCount.Should().Be(people);
        (await db2.AuctionParticipants.CountAsync(p => p.AuctionId == auctionId)).Should().Be(people);
        stored.Status.Should().Be(AuctionStatus.WaitingForStart, "the last of them filled it, and the start was scheduled once");
        (await db2.AuctionEvents.CountAsync(e => e.AuctionId == auctionId && e.EventType == AuctionEventType.RequiredParticipantsReached))
            .Should().Be(1);
        (await db2.Wallets.Where(w => users.Contains(w.UserId) && w.WalletType == WalletType.Main).SumAsync(w => w.Balance))
            .Should().Be(0m, "each paid the 20 entry fee exactly once");
    }
}
