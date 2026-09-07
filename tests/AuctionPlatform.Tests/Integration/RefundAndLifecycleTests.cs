using AuctionPlatform.Application.Bidding;
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
