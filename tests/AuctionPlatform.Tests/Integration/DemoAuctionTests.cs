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
/// Demo auctions (مزاد تجريبي): free to join and bid in, no participant cap, devices optional and
/// only on show. No money path may ever run for one: no payment, no wallet movement, no refund
/// and no prize claim.
/// </summary>
[Collection("database")]
public class DemoAuctionTests
{
    private readonly DatabaseFixture _fixture;

    public DemoAuctionTests(DatabaseFixture fixture) => _fixture = fixture;

    private static CreateAuctionRequest DemoRequest(
        IReadOnlyList<AuctionPrizeRequest>? prizes = null, decimal? entryFee = 50m, int? required = 10) =>
        new(
            Title: "مزاد تجريبي",
            Description: null,
            StartingPercentage: 10m,
            TargetPercentage: 50m,
            FinalPhasePercentage: 40m,
            BidIncrement: 100m,
            // Deliberately a fee and a cap: a demo auction must ignore both.
            EntryFee: entryFee,
            Currency: null,
            RequiredParticipants: required,
            StartDelayAfterParticipantsSeconds: 0,
            DurationSeconds: 600,
            ExpirationPolicy: ExpirationPolicy.EndAndDistribute,
            Prizes: prizes ?? Array.Empty<AuctionPrizeRequest>(),
            IsDemo: true,
            DemoValue: 1000m);

    /// <summary>Creates and publishes a demo auction; returns its id.</summary>
    private async Task<Guid> PublishedDemoAsync(IReadOnlyList<AuctionPrizeRequest>? prizes = null)
    {
        await using var scope = _fixture.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();
        var created = await admin.CreateAsync(DemoRequest(prizes));
        await admin.PublishAsync(created.Id);
        return created.Id;
    }

    private async Task<List<Guid>> UsersAsync(int count)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            // An empty wallet: joining must not need any money.
            users.Add((await TestData.CreateUserAsync(db, $"Demo {i}", mainBalance: 0m)).Id);
        }
        return users;
    }

    private async Task JoinAsync(Guid auctionId, Guid userId)
    {
        await using var scope = _fixture.CreateScope(userId);
        await scope.ServiceProvider.GetRequiredService<IParticipationService>()
            .JoinAsync(auctionId, userId, $"join-{Guid.NewGuid():N}");
    }

    private async Task<BidResultDto> BidAsync(Guid auctionId, Guid userId)
    {
        await using var scope = _fixture.CreateScope(userId);
        return await scope.ServiceProvider.GetRequiredService<IBidService>()
            .PlaceBidAsync(auctionId, userId, $"bid-{Guid.NewGuid():N}");
    }

    /// <summary>Every money row that could touch these users or this auction.</summary>
    private async Task ShouldHaveNoMoneyMovementAsync(Guid auctionId, IReadOnlyCollection<Guid> users)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        (await db.Payments.CountAsync(p => p.AuctionId == auctionId || users.Contains(p.UserId)))
            .Should().Be(0, "a demo auction never takes a payment");
        (await db.WalletTransactions.CountAsync(t => users.Contains(t.UserId) || t.ReferenceId == auctionId))
            .Should().Be(0, "a demo auction never moves a wallet");
        (await db.AuctionEntryFeeRefunds.CountAsync(r => r.AuctionId == auctionId))
            .Should().Be(0, "nothing was paid, so nothing is refunded");
        (await db.PrizeClaims.CountAsync(c => c.AuctionId == auctionId))
            .Should().Be(0, "a demo auction's devices are only on show");
        (await db.Wallets.Where(w => users.Contains(w.UserId)).SumAsync(w => w.Balance))
            .Should().Be(0m);
    }

    [Fact]
    public async Task A_demo_auction_is_free_and_open_whatever_the_request_says_and_needs_no_device()
    {
        var id = await PublishedDemoAsync();

        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var auction = await db.Auctions.AsNoTracking().SingleAsync(a => a.Id == id);

        auction.IsDemo.Should().BeTrue();
        auction.Status.Should().Be(AuctionStatus.RegistrationOpen, "it publishes without any prize");
        auction.EntryFee.Should().Be(0m, "the fee the request carried is ignored");
        auction.RequiredParticipants.Should().Be(0, "0 means open to any number");
        auction.RefundLosingParticipantsEntryFee.Should().BeFalse();
        auction.RefundWinnerEntryFee.Should().BeFalse();
        auction.OriginalPrice.Should().Be(1000m, "with no device, its value is the one typed for it");
        auction.TargetPrice.Should().Be(500m);
        auction.StartingPrice.Should().Be(50m);
        auction.MainProductNameSnapshot.Should().Be("مزاد تجريبي");

        var detail = await scope.ServiceProvider.GetRequiredService<IAuctionService>().GetAsync(id, null);
        detail.IsDemo.Should().BeTrue("the app shows the demo label from this");
    }

    [Fact]
    public async Task A_demo_auction_without_a_device_or_a_value_is_refused()
    {
        await using var scope = _fixture.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();

        var act = () => admin.CreateAsync(DemoRequest() with { DemoValue = null });

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(ErrorCodes.InvalidAuctionConfiguration);
    }

    [Fact]
    public async Task Anyone_joins_a_demo_auction_for_free_with_no_cap_and_it_never_starts_on_a_count()
    {
        var id = await PublishedDemoAsync();
        // More people than the request's cap of 10, every one with an empty wallet.
        var users = await UsersAsync(12);

        foreach (var user in users) await JoinAsync(id, user);

        // An administrator adding someone at a fee charges nothing either.
        var added = (await UsersAsync(1))[0];
        await using (var scope = _fixture.CreateScope())
        {
            var p = await scope.ServiceProvider.GetRequiredService<IParticipationService>()
                .AddByAdminAsync(id, new AddParticipantRequest(added, 30m));
            p.EntryFeePaid.Should().Be(0m);
        }

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await db.Auctions.AsNoTracking().SingleAsync(a => a.Id == id);

            auction.ParticipantCount.Should().Be(13);
            auction.Status.Should().Be(AuctionStatus.WaitingForParticipants,
                "a demo auction is still open: no count to fill, so it is never Ready on its own");
            auction.ParticipantsCompletedAt.Should().BeNull();
            auction.ScheduledStartAt.Should().BeNull();

            (await db.AuctionParticipants.Where(p => p.AuctionId == id).Select(p => p.EntryFeePaid).ToListAsync())
                .Should().OnlyContain(fee => fee == 0m);
        }

        await ShouldHaveNoMoneyMovementAsync(id, users.Append(added).ToList());
    }

    [Fact]
    public async Task A_demo_auction_starts_without_a_reason_and_is_bid_through_to_the_end_at_no_cost()
    {
        // One device, rank 1 only: the others are optional on a demo auction.
        var id = await PublishedDemoAsync(new[]
        {
            new AuctionPrizeRequest(1, "هاتف للعرض", 1000m, null, null)
        });
        var users = await UsersAsync(3);
        foreach (var user in users) await JoinAsync(id, user);

        await using (var scope = _fixture.CreateScope())
        {
            var started = await scope.ServiceProvider.GetRequiredService<IAuctionAdminService>()
                .StartNowAsync(id);
            started.Status.Should().Be(AuctionStatus.Live, "a demo auction needs no early-start reason");
        }

        // 50 → 500 in steps of 100, taking turns since nobody may bid twice in a row.
        BidResultDto last = null!;
        for (var i = 0; i < 5; i++)
        {
            last = await BidAsync(id, users[i % 2]);
        }
        last.ReachedTarget.Should().BeTrue();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await db.Auctions.AsNoTracking().SingleAsync(a => a.Id == id);

            auction.Status.Should().Be(AuctionStatus.Completed,
                "with nothing to refund or claim, it is over once its places are declared");
            auction.WinnersDeclared.Should().BeTrue();

            var winners = await db.Winners.AsNoTracking().Where(w => w.AuctionId == id).ToListAsync();
            winners.Should().ContainSingle("only the first place has a device on show");
            winners[0].UserId.Should().Be(users[0]);
            winners[0].PrizeClaimId.Should().BeNull();

            (await db.Bids.CountAsync(b => b.AuctionId == id)).Should().Be(5);
        }

        await ShouldHaveNoMoneyMovementAsync(id, users);
    }

    [Fact]
    public async Task Cancelling_a_demo_auction_refunds_nothing()
    {
        var id = await PublishedDemoAsync();
        var users = await UsersAsync(2);
        foreach (var user in users) await JoinAsync(id, user);

        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuctionAdminService>().CancelAsync(id, "test");
        }

        await ShouldHaveNoMoneyMovementAsync(id, users);
    }

    [Fact]
    public async Task An_auction_cannot_become_a_demo_once_people_have_paid_to_join()
    {
        Guid id;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db);
            auction.Status = AuctionStatus.RegistrationOpen;
            auction.ActualStartedAt = null;
            await db.SaveChangesAsync();
            var user = await TestData.CreateUserAsync(db, "Paid");
            await TestData.AddParticipantAsync(db, auction.Id, user.Id);
            id = auction.Id;
        }

        await using (var scope = _fixture.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();
            var act = () => admin.UpdateAsync(id, new UpdateAuctionRequest(
                null, null, null, null, null, null, null, null, null, null, null, null, IsDemo: true));

            (await act.Should().ThrowAsync<DomainException>())
                .Which.Code.Should().Be(ErrorCodes.InvalidAuctionState);
        }

        await using var verify = _fixture.CreateScope();
        var stored = await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .Auctions.AsNoTracking().SingleAsync(a => a.Id == id);
        stored.IsDemo.Should().BeFalse();
        stored.EntryFee.Should().Be(20m);
    }
}
