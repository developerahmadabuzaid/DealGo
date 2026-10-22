using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// Winner declaration and the entry-fee refund path, including the exactly-once guarantee that
/// makes repeated completion runs safe.
/// </summary>
[Collection("database")]
public class AuctionCompletionTests
{
    private readonly DatabaseFixture _fixture;

    public AuctionCompletionTests(DatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Six participants, four prizes. Ranking follows highest price then earliest timestamp, and
    /// the two losers get their entry fee back as shopping credit rather than cash.
    /// </summary>
    [Fact]
    public async Task Winners_take_the_prizes_and_losers_receive_shopping_credit()
    {
        Guid auctionId;
        var users = new List<Guid>();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 1000m, targetPrice: 2500m);
            auctionId = auction.Id;

            var baseTime = DateTime.UtcNow.AddMinutes(-5);

            for (var i = 0; i < 6; i++)
            {
                var user = await TestData.CreateUserAsync(db, $"P{i}", mainBalance: 100m);
                users.Add(user.Id);

                var participant = await TestData.AddParticipantAsync(db, auctionId, user.Id);

                // Descending highest prices, so the expected ranking is unambiguous.
                participant.HighestPrice = 2500m - (i * 20m);
                participant.HighestPriceAt = baseTime.AddSeconds(i);
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
            var completion = scope.ServiceProvider.GetRequiredService<IAuctionCompletionService>();
            await completion.CompleteAuctionAsync(auctionId);
        }

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var winners = await db2.Winners.AsNoTracking()
            .Where(w => w.AuctionId == auctionId)
            .OrderBy(w => w.Rank)
            .ToListAsync();

        winners.Should().HaveCount(4, "an auction awards exactly four prizes");
        winners.Select(w => w.Rank).Should().Equal(1, 2, 3, 4);
        winners.Select(w => w.HighestPrice).Should().Equal(2500m, 2480m, 2460m, 2440m);
        winners.Select(w => w.UserId).Should().Equal(users[0], users[1], users[2], users[3]);

        // Winning grants the right to the prize; the value is still owed.
        var claims = await db2.PrizeClaims.AsNoTracking()
            .Where(c => c.AuctionId == auctionId).ToListAsync();

        claims.Should().HaveCount(4);
        claims.Should().OnlyContain(c => c.Status == PrizeClaimStatus.PaymentRequired);
        claims.Should().OnlyContain(c => c.PaymentAmount > 0);

        // The auction closed at 2500 on a 5000 value, so every prize, not only the first, is
        // owed at half its price.
        var prizePrices = await db2.AuctionPrizes.AsNoTracking()
            .Where(p => p.AuctionId == auctionId)
            .ToDictionaryAsync(p => p.Id, p => p.OriginalPrice);

        foreach (var claim in claims)
        {
            claim.PaymentAmount.Should().Be(Money.Round(prizePrices[claim.PrizeId] / 2));
        }

        // The winners list the app reads says the same: the discounted amount to pay, beside the
        // full price. Showing the full price as "amount due" told a winner they owed double.
        var listed = await verify.ServiceProvider.GetRequiredService<IAuctionService>()
            .GetWinnersAsync(auctionId);
        foreach (var w in listed)
        {
            w.PrizeValue.Should().Be(prizePrices[w.PrizeId]);
            w.PaymentAmount.Should().Be(Money.Round(prizePrices[w.PrizeId] / 2));
        }

        foreach (var winner in winners)
        {
            var wallet = await db2.Wallets.AsNoTracking()
                .FirstAsync(w => w.UserId == winner.UserId && w.WalletType == WalletType.Shopping);

            wallet.Balance.Should().Be(0m, "a winner's entry fee is not refunded by default");
        }

        foreach (var loserId in new[] { users[4], users[5] })
        {
            var shopping = await db2.Wallets.AsNoTracking()
                .FirstAsync(w => w.UserId == loserId && w.WalletType == WalletType.Shopping);
            var main = await db2.Wallets.AsNoTracking()
                .FirstAsync(w => w.UserId == loserId && w.WalletType == WalletType.Main);

            shopping.Balance.Should().Be(20m, "a losing entry fee returns as shopping credit");
            main.Balance.Should().Be(100m, "the refund must not reach the main wallet");
        }

        // Scoped to this auction: the fixture database is shared across the test collection.
        var credits = await db2.WalletTransactions.AsNoTracking()
            .Where(t => t.WalletType == WalletType.Shopping &&
                        t.CreditSource == ShoppingCreditSource.AuctionEntryFeeRefund &&
                        t.ReferenceId == auctionId)
            .ToListAsync();

        credits.Should().HaveCount(2);
    }

    /// <summary>
    /// Prizes are entered against the auction, and the auction's value follows its first prize.
    /// Nothing is read from the shared catalog, and the price ladder is derived from the stored
    /// value rather than from anything the caller typed.
    /// </summary>
    [Fact]
    public async Task Creating_an_auction_takes_its_value_and_picture_from_the_first_prize()
    {
        Guid auctionId;

        await using (var scope = _fixture.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();

            var created = await admin.CreateAsync(new CreateAuctionRequest(
                Title: "مزاد الهاتف",
                Description: null,
                StartingPercentage: 10m,
                TargetPercentage: 50m,
                FinalPhasePercentage: 40m,
                BidIncrement: 20m,
                EntryFee: 15m,
                Currency: null,
                RequiredParticipants: 100,
                StartDelayAfterParticipantsSeconds: 60,
                DurationSeconds: 180,
                ExpirationPolicy: ExpirationPolicy.EndAndDistribute,
                Prizes: new[]
                {
                    new AuctionPrizeRequest(1, "هاتف ذكي", 4000m, "https://example.test/phone.jpg", "أحدث إصدار"),
                    new AuctionPrizeRequest(2, "ساعة ذكية", 1200m, null, null),
                    new AuctionPrizeRequest(3, "سماعات", 600m, null, null),
                    new AuctionPrizeRequest(4, "شاحن سريع", 150m, null, null),
                }));

            auctionId = created.Id;
        }

        await using var verify = _fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var auction = await db.Auctions.AsNoTracking().FirstAsync(a => a.Id == auctionId);

        auction.OriginalPrice.Should().Be(4000m, "the auction is worth what its first prize is worth");
        auction.MainProductNameSnapshot.Should().Be("هاتف ذكي");
        auction.MainImageSnapshot.Should().Be("https://example.test/phone.jpg");
        auction.MainProductId.Should().BeNull("prizes no longer come from the shared catalog");

        // 50% and 40% of the value that was actually stored.
        auction.TargetPrice.Should().Be(2000m);
        auction.FinalPhasePrice.Should().Be(1600m);
        auction.StartingPrice.Should().Be(200m, "it opens at 10% of the target price");
        auction.CurrentPrice.Should().Be(200m);

        var prizes = await db.AuctionPrizes.AsNoTracking()
            .Where(p => p.AuctionId == auctionId)
            .OrderBy(p => p.Rank)
            .ToListAsync();

        prizes.Should().HaveCount(4);
        prizes.Select(p => p.OriginalPrice).Should().Equal(4000m, 1200m, 600m, 150m);
        prizes.Select(p => p.ProductNameSnapshot).Should().Equal("هاتف ذكي", "ساعة ذكية", "سماعات", "شاحن سريع");
        prizes[0].Description.Should().Be("أحدث إصدار");
        prizes.Should().OnlyContain(p => p.ProductId == null);
    }

    /// <summary>A prize with no name cannot define an auction, so creation is refused.</summary>
    [Fact]
    public async Task An_auction_is_refused_when_its_first_prize_has_no_name()
    {
        await using var scope = _fixture.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();

        var act = async () => await admin.CreateAsync(new CreateAuctionRequest(
            Title: "مزاد بلا جائزة",
            Description: null,
            StartingPercentage: 10m,
            TargetPercentage: 50m,
            FinalPhasePercentage: 40m,
            BidIncrement: 20m,
            EntryFee: 15m,
            Currency: null,
            RequiredParticipants: 100,
            StartDelayAfterParticipantsSeconds: 60,
            DurationSeconds: 180,
            ExpirationPolicy: ExpirationPolicy.EndAndDistribute,
            Prizes: new[] { new AuctionPrizeRequest(1, "   ", 4000m, null, null) }));

        await act.Should().ThrowAsync<DomainException>();
    }

    /// <summary>
    /// Cancelling an auction returns every entry fee as cash in the Main wallet, not as shopping
    /// credit: the platform withdrew the service, so the money goes back the way it arrived.
    /// Repeat runs must not pay anyone twice.
    /// </summary>
    [Fact]
    public async Task Cancelling_an_auction_returns_every_entry_fee_to_the_main_wallet()
    {
        Guid auctionId;
        var users = new List<Guid>();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db);
            auctionId = auction.Id;

            for (var i = 0; i < 5; i++)
            {
                var user = await TestData.CreateUserAsync(db, $"C{i}", mainBalance: 100m);
                users.Add(user.Id);
                await TestData.AddParticipantAsync(db, auctionId, user.Id);
            }

            auction.Status = AuctionStatus.Cancelled;
            auction.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // Twice, to prove the refund marker makes a second pass a no-op.
        for (var i = 0; i < 2; i++)
        {
            await using var scope = _fixture.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IAuctionCompletionService>()
                .RefundCancelledAuctionAsync(auctionId);
        }

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var userId in users)
        {
            var main = await db2.Wallets.AsNoTracking()
                .FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Main);
            var shopping = await db2.Wallets.AsNoTracking()
                .FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Shopping);

            main.Balance.Should().Be(120m, "the entry fee comes back as withdrawable cash");
            shopping.Balance.Should().Be(0m, "a cancellation never issues store-only credit");
        }

        var refunds = await db2.AuctionEntryFeeRefunds.AsNoTracking()
            .Where(r => r.AuctionId == auctionId)
            .ToListAsync();

        refunds.Should().HaveCount(5, "each participant is refunded exactly once");
        refunds.Should().OnlyContain(r => r.DestinationWallet == WalletType.Main);
        refunds.Should().OnlyContain(r => r.Status == RefundStatus.Completed);

        var participants = await db2.AuctionParticipants.AsNoTracking()
            .Where(p => p.AuctionId == auctionId).ToListAsync();

        participants.Should().OnlyContain(p => p.Status == ParticipantStatus.Refunded);
    }

    /// <summary>
    /// Spec §130. Running completion a hundred times must still produce exactly one refund per
    /// losing participant; the unique index on (AuctionId, ParticipantId) is the guarantee.
    /// </summary>
    [Fact]
    public async Task Repeated_completion_runs_refund_each_loser_exactly_once()
    {
        Guid auctionId;
        var users = new List<Guid>();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 2500m, targetPrice: 2500m);
            auctionId = auction.Id;

            var baseTime = DateTime.UtcNow.AddMinutes(-5);

            for (var i = 0; i < 8; i++)
            {
                var user = await TestData.CreateUserAsync(db, $"R{i}", mainBalance: 100m);
                users.Add(user.Id);

                var participant = await TestData.AddParticipantAsync(db, auctionId, user.Id);
                participant.HighestPrice = 2500m - (i * 20m);
                participant.HighestPriceAt = baseTime.AddSeconds(i);
                participant.BidCount = 1;
            }

            auction.Status = AuctionStatus.Finished;
            auction.TargetReached = true;
            auction.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // Sequential repeats, then a burst of concurrent ones: both must be no-ops after the first.
        for (var i = 0; i < 20; i++)
        {
            await using var scope = _fixture.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IAuctionCompletionService>()
                .CompleteAuctionAsync(auctionId);
        }

        var barrier = new TaskCompletionSource();

        var concurrent = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var scope = _fixture.CreateScope();
            var completion = scope.ServiceProvider.GetRequiredService<IAuctionCompletionService>();
            await barrier.Task;
            try { await completion.CompleteAuctionAsync(auctionId); } catch { /* races are expected */ }
        }).ToList();

        barrier.SetResult();
        await Task.WhenAll(concurrent);

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var losers = users.Skip(4).ToList();

        var refunds = await db2.AuctionEntryFeeRefunds.AsNoTracking()
            .Where(r => r.AuctionId == auctionId).ToListAsync();

        refunds.Should().HaveCount(4, "exactly one refund row per losing participant");
        refunds.Select(r => r.ParticipantId).Should().OnlyHaveUniqueItems();

        foreach (var loserId in losers)
        {
            var wallet = await db2.Wallets.AsNoTracking()
                .FirstAsync(w => w.UserId == loserId && w.WalletType == WalletType.Shopping);

            wallet.Balance.Should().Be(20m, "the entry fee is credited once, no matter how many runs");

            var entries = await db2.WalletTransactions.AsNoTracking()
                .Where(t => t.UserId == loserId && t.WalletType == WalletType.Shopping)
                .ToListAsync();

            entries.Should().HaveCount(1);
        }

        // Winner declaration is likewise once-only.
        (await db2.Winners.CountAsync(w => w.AuctionId == auctionId)).Should().Be(4);
        (await db2.PrizeClaims.CountAsync(c => c.AuctionId == auctionId)).Should().Be(4);
    }

    /// <summary>
    /// An auction that ends with fewer than four bidders awards only as many prizes as there
    /// were genuine participants; non-bidders are never winners.
    /// </summary>
    [Fact]
    public async Task Only_participants_who_bid_can_win()
    {
        Guid auctionId;
        var users = new List<Guid>();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 1200m, targetPrice: 2500m);
            auctionId = auction.Id;

            for (var i = 0; i < 5; i++)
            {
                var user = await TestData.CreateUserAsync(db, $"B{i}", mainBalance: 100m);
                users.Add(user.Id);

                var participant = await TestData.AddParticipantAsync(db, auctionId, user.Id);

                // Only the first two ever placed a bid.
                if (i < 2)
                {
                    participant.HighestPrice = 1200m - (i * 20m);
                    participant.HighestPriceAt = DateTime.UtcNow.AddMinutes(-1).AddSeconds(i);
                    participant.BidCount = 1;
                }
            }

            auction.Status = AuctionStatus.Finished;
            auction.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuctionCompletionService>()
                .CompleteAuctionAsync(auctionId);
        }

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var winners = await db2.Winners.AsNoTracking()
            .Where(w => w.AuctionId == auctionId).ToListAsync();

        winners.Should().HaveCount(2);
        winners.Select(w => w.UserId).Should().BeEquivalentTo(new[] { users[0], users[1] });

        // The three non-bidders are losers and are refunded like any other loser.
        foreach (var id in users.Skip(2))
        {
            var wallet = await db2.Wallets.AsNoTracking()
                .FirstAsync(w => w.UserId == id && w.WalletType == WalletType.Shopping);
            wallet.Balance.Should().Be(20m);
        }
    }
}
