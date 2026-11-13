using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Application.Prizes;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// A winner who does not pay for their device within 24 hours loses the claim and gets their entry
/// fee back in the Shopping wallet, exactly once.
/// </summary>
[Collection("database")]
public class PrizeClaimExpiryTests
{
    private const decimal EntryFee = 20m;

    private readonly DatabaseFixture _fixture;

    public PrizeClaimExpiryTests(DatabaseFixture fixture) => _fixture = fixture;

    /// <summary>A closed auction with four winners and their claims, all still to be paid.</summary>
    private async Task<(Guid AuctionId, List<Guid> Winners)> FinishedAuctionAsync(bool refundWinners = false)
    {
        Guid auctionId;
        var users = new List<Guid>();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 1000m, targetPrice: 2500m);
            auctionId = auction.Id;
            auction.RefundWinnerEntryFee = refundWinners;

            for (var i = 0; i < 4; i++)
            {
                var user = await TestData.CreateUserAsync(db, $"Winner {i}", mainBalance: 10_000m);
                users.Add(user.Id);
                var participant = await TestData.AddParticipantAsync(db, auctionId, user.Id, EntryFee);
                participant.HighestPrice = 2500m - i * 20m;
                participant.HighestPriceAt = DateTime.UtcNow.AddSeconds(-60 + i);
                participant.BidCount = 1;
            }

            auction.Status = AuctionStatus.Finished;
            auction.TargetReached = true;
            auction.FinishedAt = DateTime.UtcNow;
            auction.CurrentPrice = 2500m;
            await db.SaveChangesAsync();
        }

        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuctionCompletionService>().CompleteAuctionAsync(auctionId);
        }

        return (auctionId, users);
    }

    private async Task MakeOverdueAsync(Guid auctionId, Guid? onlyUser = null)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.PrizeClaims
            .Where(c => c.AuctionId == auctionId && (onlyUser == null || c.UserId == onlyUser))
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.PaymentDeadlineAt, DateTime.UtcNow.AddMinutes(-1)));
    }

    private async Task<int> ExpireAsync()
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPrizeClaimExpiryService>().ExpireOverdueAsync();
    }

    private async Task<decimal> ShoppingBalanceAsync(Guid userId)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Wallets.AsNoTracking()
            .Where(w => w.UserId == userId && w.WalletType == WalletType.Shopping)
            .Select(w => w.Balance).SingleAsync();
    }

    private async Task<PrizeClaimStatus> ClaimStatusAsync(Guid auctionId, Guid userId)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.PrizeClaims.AsNoTracking()
            .Where(c => c.AuctionId == auctionId && c.UserId == userId).Select(c => c.Status).SingleAsync();
    }

    [Fact]
    public async Task An_unpaid_winner_gets_the_entry_fee_back_as_shopping_credit_once_the_24_hours_pass()
    {
        var (auctionId, winners) = await FinishedAuctionAsync();
        var winner = winners[0];
        var before = await ShoppingBalanceAsync(winner);

        // Still inside the 24 hours: nothing happens.
        await ExpireAsync();
        (await ClaimStatusAsync(auctionId, winner)).Should().Be(PrizeClaimStatus.PaymentRequired);

        await MakeOverdueAsync(auctionId, winner);
        await ExpireAsync();
        await ExpireAsync();   // a second pass changes nothing

        (await ClaimStatusAsync(auctionId, winner)).Should().Be(PrizeClaimStatus.Expired);
        (await ShoppingBalanceAsync(winner)).Should().Be(before + EntryFee);

        // The other winners still have their time.
        (await ClaimStatusAsync(auctionId, winners[1])).Should().Be(PrizeClaimStatus.PaymentRequired);

        await using var verify = _fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AuctionEntryFeeRefunds.CountAsync(r => r.AuctionId == auctionId && r.UserId == winner)).Should().Be(1);
        var claim = await db.PrizeClaims.AsNoTracking().SingleAsync(c => c.AuctionId == auctionId && c.UserId == winner);
        claim.ExpiredAt.Should().NotBeNull();
        var note = await db.Notifications.AsNoTracking()
            .SingleAsync(n => n.UserId == winner && n.Type == NotificationType.PrizeClaimExpired);
        note.Body.Should().Contain("20.00");
    }

    [Fact]
    public async Task A_paid_winner_is_never_expired()
    {
        var (auctionId, winners) = await FinishedAuctionAsync();
        var winner = winners[0];

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var claimId = await db.PrizeClaims.Where(c => c.AuctionId == auctionId && c.UserId == winner).Select(c => c.Id).SingleAsync();
            await scope.ServiceProvider.GetRequiredService<IPrizeClaimService>()
                .PayAsync(winner, claimId, new PayPrizeRequest($"pay-{Guid.NewGuid():N}", null));
        }

        var before = await ShoppingBalanceAsync(winner);
        await MakeOverdueAsync(auctionId, winner);
        await ExpireAsync();

        (await ClaimStatusAsync(auctionId, winner)).Should().Be(PrizeClaimStatus.Paid);
        (await ShoppingBalanceAsync(winner)).Should().Be(before);
    }

    [Fact]
    public async Task Paying_after_the_24_hours_is_refused()
    {
        var (auctionId, winners) = await FinishedAuctionAsync();
        var winner = winners[0];
        await MakeOverdueAsync(auctionId, winner);

        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var claimId = await db.PrizeClaims.Where(c => c.AuctionId == auctionId && c.UserId == winner).Select(c => c.Id).SingleAsync();

        var pay = () => scope.ServiceProvider.GetRequiredService<IPrizeClaimService>()
            .PayAsync(winner, claimId, new PayPrizeRequest($"pay-{Guid.NewGuid():N}", null));

        (await pay.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.PrizeClaimNotPayable);
    }

    [Fact]
    public async Task The_entry_fee_comes_off_the_device_price_unless_the_auction_returns_it()
    {
        var (deducted, deductedWinners) = await FinishedAuctionAsync();
        var (returned, returnedWinners) = await FinishedAuctionAsync(refundWinners: true);

        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var a = await db.PrizeClaims.AsNoTracking().SingleAsync(c => c.AuctionId == deducted && c.UserId == deductedWinners[0]);
        var b = await db.PrizeClaims.AsNoTracking().SingleAsync(c => c.AuctionId == returned && c.UserId == returnedWinners[0]);

        a.EntryFeeDeducted.Should().Be(EntryFee);
        b.EntryFeeDeducted.Should().Be(0m);
        a.PaymentAmount.Should().Be(b.PaymentAmount - EntryFee, "the same device and closing price, less the fee");

        // What the winner is charged is the lower amount.
        var main = await db.Wallets.AsNoTracking()
            .Where(w => w.UserId == deductedWinners[0] && w.WalletType == WalletType.Main).Select(w => w.Balance).SingleAsync();
        await scope.ServiceProvider.GetRequiredService<IPrizeClaimService>()
            .PayAsync(deductedWinners[0], a.Id, new PayPrizeRequest($"pay-{Guid.NewGuid():N}", null));
        var after = await db.Wallets.AsNoTracking()
            .Where(w => w.UserId == deductedWinners[0] && w.WalletType == WalletType.Main).Select(w => w.Balance).SingleAsync();
        (main - after).Should().Be(a.PaymentAmount);

        var shown = await scope.ServiceProvider.GetRequiredService<IPrizeClaimService>().GetClaimAsync(deductedWinners[0], a.Id);
        shown.EntryFeeDeducted.Should().Be(EntryFee);
    }

    [Fact]
    public async Task A_winner_whose_fee_was_already_returned_is_not_paid_twice()
    {
        // This auction gives every winner their entry fee back when it closes.
        var (auctionId, winners) = await FinishedAuctionAsync(refundWinners: true);
        var winner = winners[0];
        var before = await ShoppingBalanceAsync(winner);

        await MakeOverdueAsync(auctionId, winner);
        await ExpireAsync();

        (await ClaimStatusAsync(auctionId, winner)).Should().Be(PrizeClaimStatus.Expired);
        (await ShoppingBalanceAsync(winner)).Should().Be(before);
    }
}
