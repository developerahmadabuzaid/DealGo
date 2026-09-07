using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Application.Store;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The critical concurrency scenarios from the specification, run against a real PostgreSQL
/// instance because the guarantees under test are row locks and unique constraints.
/// </summary>
[Collection("database")]
public class BidConcurrencyTests
{
    private readonly DatabaseFixture _fixture;

    public BidConcurrencyTests(DatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Spec §127. Price 2480, target 2500, increment 20. One hundred bids arrive at once.
    /// Exactly one may reach the target; the auction must then be closed to everyone else.
    /// </summary>
    [Fact]
    public async Task One_hundred_simultaneous_bids_produce_exactly_one_target_hit()
    {
        Guid auctionId;
        var participantUsers = new List<Guid>();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 2480m, targetPrice: 2500m);
            auctionId = auction.Id;

            for (var i = 0; i < 100; i++)
            {
                var user = await TestData.CreateUserAsync(db, $"Bidder {i}", mainBalance: 100m);
                await TestData.AddParticipantAsync(db, auctionId, user.Id);
                participantUsers.Add(user.Id);
            }
        }

        // Each task gets its own scope, so each runs on its own connection: a genuine race.
        var barrier = new TaskCompletionSource();

        var attempts = participantUsers.Select(async (userId, index) =>
        {
            await using var scope = _fixture.CreateScope(userId);
            var bids = scope.ServiceProvider.GetRequiredService<IBidService>();

            await barrier.Task;

            try
            {
                var result = await bids.PlaceBidAsync(auctionId, userId, $"race-{index}");
                return (Success: true, result.ReachedTarget, Code: (string?)null);
            }
            catch (DomainException ex)
            {
                return (Success: false, ReachedTarget: false, Code: ex.Code);
            }
        }).ToList();

        barrier.SetResult();
        var results = await Task.WhenAll(attempts);

        var accepted = results.Count(r => r.Success);
        var targetHits = results.Count(r => r.ReachedTarget);

        // 2480 -> 2500 is a single increment, so exactly one bid can be accepted.
        accepted.Should().Be(1, "only one bid can move the price from 2480 to the 2500 target");
        targetHits.Should().Be(1, "the target may be reached exactly once");

        results.Where(r => !r.Success).Should().OnlyContain(r =>
            r.Code == ErrorCodes.AuctionFinished ||
            r.Code == ErrorCodes.AuctionNotActive ||
            r.Code == ErrorCodes.TargetAlreadyReached);

        await using var verify = _fixture.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var final = await verifyDb.Auctions.AsNoTracking().FirstAsync(a => a.Id == auctionId);
        final.CurrentPrice.Should().Be(2500m);
        final.TargetReached.Should().BeTrue();
        final.Status.Should().BeOneOf(
            AuctionStatus.Finished, AuctionStatus.WinnersDeclared, AuctionStatus.PrizeClaim);

        var bidRows = await verifyDb.Bids.AsNoTracking()
            .Where(b => b.AuctionId == auctionId).ToListAsync();

        bidRows.Should().HaveCount(1);
        bidRows.Select(b => b.SequenceNumber).Should().OnlyHaveUniqueItems();
        bidRows.Count(b => b.ReachedTarget).Should().Be(1);
    }

    /// <summary>
    /// A wider race: enough headroom for many bids, checking that the sequence stays gapless and
    /// unique and that the price advances by exactly one increment per accepted bid.
    /// </summary>
    [Fact]
    public async Task Concurrent_bids_produce_a_gapless_unique_sequence()
    {
        Guid auctionId;
        var users = new List<Guid>();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // 1000 -> 2500 at 20 a bid leaves room for 75 accepted bids.
            var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 1000m, targetPrice: 2500m);
            auctionId = auction.Id;

            for (var i = 0; i < 40; i++)
            {
                var user = await TestData.CreateUserAsync(db, $"Bidder {i}", mainBalance: 100m);
                await TestData.AddParticipantAsync(db, auctionId, user.Id);
                users.Add(user.Id);
            }
        }

        var barrier = new TaskCompletionSource();

        var attempts = users.Select(async (userId, index) =>
        {
            await using var scope = _fixture.CreateScope(userId);
            var bids = scope.ServiceProvider.GetRequiredService<IBidService>();
            await barrier.Task;

            try { await bids.PlaceBidAsync(auctionId, userId, $"seq-{index}"); return true; }
            catch (DomainException) { return false; }
        }).ToList();

        barrier.SetResult();
        var results = await Task.WhenAll(attempts);
        results.Count(r => r).Should().Be(40);

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var bidRows = await db2.Bids.AsNoTracking()
            .Where(b => b.AuctionId == auctionId)
            .OrderBy(b => b.SequenceNumber)
            .ToListAsync();

        bidRows.Should().HaveCount(40);
        bidRows.Select(b => b.SequenceNumber).Should().Equal(Enumerable.Range(1, 40).Select(i => (long)i));

        // Every bid must chain onto the previous price with no lost or duplicated updates.
        for (var i = 0; i < bidRows.Count; i++)
        {
            bidRows[i].PreviousPrice.Should().Be(1000m + 20m * i);
            bidRows[i].NewPrice.Should().Be(1000m + 20m * (i + 1));
        }

        var auctionRow = await db2.Auctions.AsNoTracking().FirstAsync(a => a.Id == auctionId);
        auctionRow.CurrentPrice.Should().Be(1800m);
    }

    /// <summary>Spec §24: retrying a bid with the same idempotency key must not create a second bid.</summary>
    [Fact]
    public async Task Replaying_a_bid_with_the_same_idempotency_key_returns_the_original()
    {
        Guid auctionId, userId;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 1000m, targetPrice: 2500m);
            auctionId = auction.Id;

            var user = await TestData.CreateUserAsync(db, "Retrier", mainBalance: 100m);
            userId = user.Id;
            await TestData.AddParticipantAsync(db, auctionId, userId);
        }

        await using var bidScope = _fixture.CreateScope(userId);
        var bids = bidScope.ServiceProvider.GetRequiredService<IBidService>();

        var first = await bids.PlaceBidAsync(auctionId, userId, "same-key");
        var second = await bids.PlaceBidAsync(auctionId, userId, "same-key");
        var third = await bids.PlaceBidAsync(auctionId, userId, "same-key");

        first.WasDuplicate.Should().BeFalse();
        second.WasDuplicate.Should().BeTrue();
        third.WasDuplicate.Should().BeTrue();

        second.BidId.Should().Be(first.BidId);
        second.SequenceNumber.Should().Be(first.SequenceNumber);
        second.NewPrice.Should().Be(first.NewPrice);

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        (await db2.Bids.CountAsync(b => b.AuctionId == auctionId)).Should().Be(1);
        (await db2.Auctions.AsNoTracking().FirstAsync(a => a.Id == auctionId)).CurrentPrice.Should().Be(1020m);
    }

    /// <summary>Concurrent retries of one key race through the unique index, not past it.</summary>
    [Fact]
    public async Task Concurrent_retries_of_one_idempotency_key_create_a_single_bid()
    {
        Guid auctionId, userId;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 1000m, targetPrice: 2500m);
            auctionId = auction.Id;

            var user = await TestData.CreateUserAsync(db, "Racer", mainBalance: 100m);
            userId = user.Id;
            await TestData.AddParticipantAsync(db, auctionId, userId);
        }

        var barrier = new TaskCompletionSource();

        var attempts = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var scope = _fixture.CreateScope(userId);
            var bids = scope.ServiceProvider.GetRequiredService<IBidService>();
            await barrier.Task;

            try { return await bids.PlaceBidAsync(auctionId, userId, "one-key"); }
            catch (DomainException) { return null; }
        }).ToList();

        barrier.SetResult();
        var results = (await Task.WhenAll(attempts)).Where(r => r is not null).ToList();

        results.Should().NotBeEmpty();
        results.Select(r => r!.BidId).Distinct().Should().HaveCount(1);

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db2.Bids.CountAsync(b => b.AuctionId == auctionId)).Should().Be(1);
    }
}

[Collection("database")]
public class WalletConcurrencyTests
{
    private readonly DatabaseFixture _fixture;

    public WalletConcurrencyTests(DatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Spec §128. A 30 shopping balance with two simultaneous 25 debits: one succeeds, one fails,
    /// and the balance lands on 5 rather than -20.
    /// </summary>
    [Fact]
    public async Task The_same_balance_cannot_be_spent_twice()
    {
        Guid userId;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await TestData.CreateUserAsync(db, "Spender", shoppingBalance: 30m);
            userId = user.Id;
        }

        var barrier = new TaskCompletionSource();

        var attempts = Enumerable.Range(0, 2).Select(async i =>
        {
            await using var scope = _fixture.CreateScope(userId);
            var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();
            await barrier.Task;

            try
            {
                await wallets.DebitAsync(new WalletMovement
                {
                    UserId = userId,
                    WalletType = WalletType.Shopping,
                    Amount = 25m,
                    Currency = "USD",
                    TransactionType = WalletTransactionType.StorePurchase,
                    IdempotencyKey = $"spend-{i}"
                });
                return true;
            }
            catch (DomainException) { return false; }
        }).ToList();

        barrier.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Count(r => r).Should().Be(1, "only one 25 debit fits inside a 30 balance");

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var wallet = await db2.Wallets.AsNoTracking()
            .FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Shopping);

        wallet.Balance.Should().Be(5m);
    }

    /// <summary>Every balance change must be explained by exactly one ledger entry.</summary>
    [Fact]
    public async Task The_ledger_always_reconciles_to_the_balance()
    {
        Guid userId;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await TestData.CreateUserAsync(db, "Ledger", mainBalance: 1000m);
            userId = user.Id;
        }

        var barrier = new TaskCompletionSource();

        var work = Enumerable.Range(0, 30).Select(async i =>
        {
            await using var scope = _fixture.CreateScope(userId);
            var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();
            await barrier.Task;

            var movement = new WalletMovement
            {
                UserId = userId,
                WalletType = WalletType.Main,
                Amount = 10m,
                Currency = "USD",
                TransactionType = WalletTransactionType.Adjustment,
                IdempotencyKey = $"move-{i}"
            };

            try
            {
                if (i % 2 == 0) await wallets.CreditAsync(movement);
                else await wallets.DebitAsync(movement);
            }
            catch (DomainException) { /* insufficient funds is a legitimate outcome */ }
        }).ToList();

        barrier.SetResult();
        await Task.WhenAll(work);

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var wallet = await db2.Wallets.AsNoTracking()
            .FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Main);

        var ledgerSum = await db2.WalletTransactions.AsNoTracking()
            .Where(t => t.UserId == userId && t.WalletType == WalletType.Main)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        // Starting balance 1000 plus the net of every ledger entry must equal the stored balance.
        wallet.Balance.Should().Be(1000m + ledgerSum);
    }

    /// <summary>An idempotency key makes a repeated credit a no-op, not a second payout.</summary>
    [Fact]
    public async Task A_repeated_credit_with_one_key_moves_money_once()
    {
        Guid userId;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await TestData.CreateUserAsync(db, "Credited");
            userId = user.Id;
        }

        for (var i = 0; i < 10; i++)
        {
            await using var scope = _fixture.CreateScope(userId);
            var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();

            await wallets.CreditAsync(new WalletMovement
            {
                UserId = userId,
                WalletType = WalletType.Shopping,
                Amount = 20m,
                Currency = "USD",
                TransactionType = WalletTransactionType.ShoppingCredit,
                CreditSource = ShoppingCreditSource.AuctionEntryFeeRefund,
                IdempotencyKey = "one-and-only"
            });
        }

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var wallet = await db2.Wallets.AsNoTracking()
            .FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Shopping);

        wallet.Balance.Should().Be(20m);

        (await db2.WalletTransactions.CountAsync(t => t.UserId == userId)).Should().Be(1);
    }
}

[Collection("database")]
public class StoreConcurrencyTests
{
    private readonly DatabaseFixture _fixture;

    public StoreConcurrencyTests(DatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Spec §129. One unit in stock, two simultaneous buyers: one order, one OUT_OF_STOCK,
    /// and stock ends at zero.
    /// </summary>
    [Fact]
    public async Task Two_buyers_cannot_both_take_the_last_unit()
    {
        Guid productId;
        var buyers = new List<Guid>();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var product = await TestData.CreateStoreProductAsync(db, price: 20m, stock: 1);
            productId = product.Id;

            for (var i = 0; i < 2; i++)
            {
                var user = await TestData.CreateUserAsync(db, $"Buyer {i}", shoppingBalance: 100m);
                buyers.Add(user.Id);
            }
        }

        var barrier = new TaskCompletionSource();

        var attempts = buyers.Select(async (userId, index) =>
        {
            await using var scope = _fixture.CreateScope(userId);
            var orders = scope.ServiceProvider.GetRequiredService<IStoreOrderService>();
            await barrier.Task;

            try
            {
                await orders.CheckoutAsync(userId, new CheckoutRequest(
                    new[] { new CheckoutItemRequest(productId, 1) },
                    StorePaymentMethod.ShoppingWallet, null, $"buy-{index}"));
                return (Ok: true, Code: (string?)null);
            }
            catch (DomainException ex) { return (Ok: false, Code: ex.Code); }
        }).ToList();

        barrier.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Count(r => r.Ok).Should().Be(1);
        results.Where(r => !r.Ok).Should().OnlyContain(r =>
            r.Code == ErrorCodes.OutOfStock || r.Code == ErrorCodes.ProductNotAvailable);

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var product2 = await db2.StoreProducts.AsNoTracking().FirstAsync(p => p.Id == productId);
        product2.StockQuantity.Should().Be(0);
        product2.SoldQuantity.Should().Be(1);
        product2.Status.Should().Be(StoreProductStatus.OutOfStock);

        (await db2.StoreOrders.CountAsync()).Should().Be(1);
    }

    /// <summary>Spec §132: a shopping-wallet purchase debits the shopping wallet and nothing else.</summary>
    [Fact]
    public async Task A_shopping_wallet_purchase_only_touches_shopping_credit()
    {
        Guid productId, userId;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var product = await TestData.CreateStoreProductAsync(db, price: 35m, stock: 10);
            productId = product.Id;

            var user = await TestData.CreateUserAsync(db, "Shopper", mainBalance: 200m, shoppingBalance: 50m);
            userId = user.Id;
        }

        await using var buyScope = _fixture.CreateScope(userId);
        var orders = buyScope.ServiceProvider.GetRequiredService<IStoreOrderService>();

        var order = await orders.CheckoutAsync(userId, new CheckoutRequest(
            new[] { new CheckoutItemRequest(productId, 1) },
            StorePaymentMethod.ShoppingWallet, null, "single-buy"));

        order.Status.Should().Be(StoreOrderStatus.Paid);
        order.Total.Should().Be(35m);

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var shopping = await db2.Wallets.AsNoTracking()
            .FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Shopping);
        var main = await db2.Wallets.AsNoTracking()
            .FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Main);

        shopping.Balance.Should().Be(15m);
        main.Balance.Should().Be(200m, "a shopping purchase must never touch the main wallet");

        var ledger = await db2.WalletTransactions.AsNoTracking()
            .Where(t => t.UserId == userId).ToListAsync();

        ledger.Should().ContainSingle(t =>
            t.TransactionType == WalletTransactionType.StorePurchase &&
            t.Amount == -35m &&
            t.WalletType == WalletType.Shopping);
    }

    /// <summary>Spec §178: a retried checkout returns the original order.</summary>
    [Fact]
    public async Task Retrying_checkout_with_one_key_creates_a_single_order()
    {
        Guid productId, userId;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            productId = (await TestData.CreateStoreProductAsync(db, price: 10m, stock: 50)).Id;
            userId = (await TestData.CreateUserAsync(db, "Retryer", shoppingBalance: 100m)).Id;
        }

        var barrier = new TaskCompletionSource();

        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var scope = _fixture.CreateScope(userId);
            var orders = scope.ServiceProvider.GetRequiredService<IStoreOrderService>();
            await barrier.Task;

            try
            {
                return await orders.CheckoutAsync(userId, new CheckoutRequest(
                    new[] { new CheckoutItemRequest(productId, 1) },
                    StorePaymentMethod.ShoppingWallet, null, "retry-key"));
            }
            catch (Exception) { return null; }
        }).ToList();

        barrier.SetResult();
        var results = (await Task.WhenAll(attempts)).Where(o => o is not null).ToList();

        results.Select(o => o!.Id).Distinct().Should().HaveCount(1);

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        (await db2.StoreOrders.CountAsync(o => o.UserId == userId)).Should().Be(1);

        var wallet = await db2.Wallets.AsNoTracking()
            .FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Shopping);

        wallet.Balance.Should().Be(90m, "only one order may be charged");
    }
}
