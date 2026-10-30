using System.Security.Claims;
using AuctionPlatform.Api.BackgroundServices;
using AuctionPlatform.Api.Controllers;
using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Prizes;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using AuctionPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The money rules that hold whatever the caller does: the ledger cannot be rewritten, balances
/// cannot go negative, no public endpoint can add money, and every auction charge happens once.
/// </summary>
[Collection("database")]
public class FinancialIntegrityTests
{
    private readonly DatabaseFixture _fixture;

    public FinancialIntegrityTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task<Guid> UserWithLedgeredBalanceAsync(decimal amount)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await TestData.CreateUserAsync(db, "Payer");
        await scope.ServiceProvider.GetRequiredService<IWalletService>().CreditAsync(new WalletMovement
        {
            UserId = user.Id,
            WalletType = WalletType.Main,
            Amount = amount,
            TransactionType = WalletTransactionType.Deposit,
            IdempotencyKey = $"seed:{user.Id:N}"
        });
        return user.Id;
    }

    private async Task<decimal> BalanceAsync(Guid userId, WalletType type = WalletType.Main)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await db.Wallets.AsNoTracking().FirstAsync(w => w.UserId == userId && w.WalletType == type)).Balance;
    }

    // ------------------------------------------------------------------ the ledger in the database

    [Fact]
    public async Task A_ledger_entry_cannot_be_deleted_or_have_its_amount_changed()
    {
        var userId = await UserWithLedgeredBalanceAsync(100m);

        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entry = await db.WalletTransactions.AsNoTracking().FirstAsync(t => t.UserId == userId);

        var rewrite = () => db.WalletTransactions.Where(t => t.Id == entry.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Amount, 1_000_000m));
        await rewrite.Should().ThrowAsync<Exception>("a ledger entry's amount is fixed once written");

        var delete = () => db.WalletTransactions.Where(t => t.Id == entry.Id).ExecuteDeleteAsync();
        await delete.Should().ThrowAsync<Exception>("the ledger is append-only");

        // The one change the expiry worker makes is still allowed.
        await db.WalletTransactions.Where(t => t.Id == entry.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, (DateTime?)null));
    }

    [Fact]
    public async Task A_payment_record_cannot_be_deleted_or_have_its_amount_changed()
    {
        var userId = await UserWithLedgeredBalanceAsync(0.01m);
        Guid paymentId;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var payment = new Payment { UserId = userId, PaymentType = PaymentType.Deposit, Amount = 10m, Provider = "PAYMOB" };
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }

        await using var check = _fixture.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var raise = () => context.Payments.Where(p => p.Id == paymentId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Amount, 10_000m));
        await raise.Should().ThrowAsync<Exception>();

        var delete = () => context.Payments.Where(p => p.Id == paymentId).ExecuteDeleteAsync();
        await delete.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task A_wallet_balance_can_never_be_negative_even_written_directly()
    {
        var userId = await UserWithLedgeredBalanceAsync(5m);

        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var act = () => db.Wallets.Where(w => w.UserId == userId && w.WalletType == WalletType.Main)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Balance, -1m));

        await act.Should().ThrowAsync<Exception>("CK_Wallets_NonNegative");
    }

    // ------------------------------------------------------------------ endpoints that must not add money

    private static WalletController WalletControllerFor(
        IServiceProvider services, Guid userId, string environment, bool allowSandbox)
    {
        var controller = new WalletController(
            services.GetRequiredService<IWalletService>(),
            services.GetRequiredService<IWalletTopUpService>(),
            new TestEnvironment(environment),
            Options.Create(new PaymentOptions { AllowSandboxDeposits = allowSandbox }))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
                }
            }
        };
        return controller;
    }

    [Theory]
    [InlineData("Production", true)]   // whatever the setting says
    [InlineData("Development", false)]
    public async Task The_free_deposit_endpoint_adds_nothing(string environment, bool allowSandbox)
    {
        var userId = await UserWithLedgeredBalanceAsync(1m);

        await using var scope = _fixture.CreateScope();
        var controller = WalletControllerFor(scope.ServiceProvider, userId, environment, allowSandbox);

        var result = await controller.Deposit(new DepositRequest(1_000_000m, null, null, "abuse-1"), default);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await BalanceAsync(userId)).Should().Be(1m);
    }

    [Fact]
    public async Task The_generic_webhook_refuses_a_payload_signed_with_a_guessable_secret()
    {
        var userId = await UserWithLedgeredBalanceAsync(1m);
        const string guessable = "not-configured";

        var body = $$"""{"eventId":"evt-{{Guid.NewGuid():N}}","transactionId":"x","status":"succeeded","userId":"{{userId}}","amount":5000,"currency":"SAR"}""";
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(guessable));
        var signature = "sha256=" + Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(body)));

        await using var scope = _fixture.CreateScope();
        var service = new PaymentWebhookService(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            scope.ServiceProvider.GetRequiredService<IWalletService>(),
            scope.ServiceProvider.GetRequiredService<IDateTimeProvider>(),
            Options.Create(new PaymentOptions { WebhookSecret = guessable }),
            NullLogger<PaymentWebhookService>.Instance);

        var result = await service.HandleAsync("anything", body, signature);

        result.Accepted.Should().BeFalse();
        (await BalanceAsync(userId)).Should().Be(1m);
    }

    // ------------------------------------------------------------------ administrative adjustments

    [Fact]
    public async Task An_admin_adjustment_needs_a_key_and_is_applied_and_audited_once()
    {
        var userId = await UserWithLedgeredBalanceAsync(0.01m);

        await using (var scope = _fixture.CreateScope(actingUserId: Guid.NewGuid()))
        {
            var admin = scope.ServiceProvider.GetRequiredService<IAdminWalletService>();

            var keyless = () => admin.AdjustAsync(userId, new AdminCreditRequest(50m, null, WalletType.Main, "goodwill", ""));
            (await keyless.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);

            var request = new AdminCreditRequest(50m, null, WalletType.Main, "goodwill", "adj-once-1");
            await admin.AdjustAsync(userId, request);
            (await admin.AdjustAsync(userId, request)).WasDuplicate.Should().BeTrue();
        }

        (await BalanceAsync(userId)).Should().Be(50.01m);

        await using var check = _fixture.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var walletId = (await db.Wallets.AsNoTracking().FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Main)).Id;
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.EntityId == walletId && a.Action == AuditActionType.WalletAdjusted))
            .Should().Be(1);
    }

    // ------------------------------------------------------------------ auctions

    private async Task<Guid> OpenAuctionAsync(decimal entryFee)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var auction = await TestData.CreateLiveAuctionAsync(db, requiredParticipants: 50);
        auction.Status = AuctionStatus.RegistrationOpen;
        auction.ActualStartedAt = null;
        auction.EntryFee = entryFee;
        await db.SaveChangesAsync();
        return auction.Id;
    }

    [Fact]
    public async Task Simultaneous_joins_by_one_user_charge_the_entry_fee_once()
    {
        var auctionId = await OpenAuctionAsync(20m);
        var userId = await UserWithLedgeredBalanceAsync(100m);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            await using var scope = _fixture.CreateScope(actingUserId: userId);
            try
            {
                await scope.ServiceProvider.GetRequiredService<IParticipationService>()
                    .JoinAsync(auctionId, userId, $"join-{i}");
            }
            catch (DomainException) { /* already joined */ }
            catch (DbUpdateException) { /* lost the race to the unique index */ }
        })));

        (await BalanceAsync(userId)).Should().Be(80m);

        await using var check = _fixture.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AuctionParticipants.CountAsync(p => p.AuctionId == auctionId && p.UserId == userId)).Should().Be(1);
    }

    [Fact]
    public async Task Joining_without_enough_balance_charges_nothing_and_adds_no_participant()
    {
        var auctionId = await OpenAuctionAsync(20m);
        var userId = await UserWithLedgeredBalanceAsync(5m);

        await using (var scope = _fixture.CreateScope(actingUserId: userId))
        {
            var act = () => scope.ServiceProvider.GetRequiredService<IParticipationService>().JoinAsync(auctionId, userId, "join-poor");
            (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.InsufficientBalance);
        }

        (await BalanceAsync(userId)).Should().Be(5m);
        await using var check = _fixture.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AuctionParticipants.AnyAsync(p => p.AuctionId == auctionId && p.UserId == userId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_won_device_is_paid_for_once_and_only_by_its_winner()
    {
        // An auction with one bidder who wins first place.
        Guid auctionId, winnerId;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 1000m, targetPrice: 2500m);
            auctionId = auction.Id;
            winnerId = (await TestData.CreateUserAsync(db, "Winner")).Id;
            var participant = await TestData.AddParticipantAsync(db, auctionId, winnerId);
            participant.HighestPrice = 2500m;
            participant.HighestPriceAt = DateTime.UtcNow.AddMinutes(-1);
            participant.BidCount = 1;
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

        // Fund the winner properly, through the ledger, with enough for two payments.
        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWalletService>().CreditAsync(new WalletMovement
            {
                UserId = winnerId, WalletType = WalletType.Main, Amount = 10_000m,
                TransactionType = WalletTransactionType.Deposit, IdempotencyKey = $"fund:{winnerId:N}"
            });
        }

        Guid claimId; decimal due;
        await using (var scope = _fixture.CreateScope())
        {
            var claim = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PrizeClaims.AsNoTracking()
                .SingleAsync(c => c.AuctionId == auctionId && c.UserId == winnerId);
            claimId = claim.Id;
            due = claim.PaymentAmount;
        }

        var before = await BalanceAsync(winnerId);

        // Someone else cannot pay (or see) the winner's claim.
        await using (var scope = _fixture.CreateScope())
        {
            var stranger = () => scope.ServiceProvider.GetRequiredService<IPrizeClaimService>()
                .PayAsync(Guid.NewGuid(), claimId, new PayPrizeRequest("stranger", null));
            await stranger.Should().ThrowAsync<DomainException>();
        }

        // The winner taps "pay" several times at once, with different keys.
        await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Task.Run(async () =>
        {
            await using var scope = _fixture.CreateScope(actingUserId: winnerId);
            try
            {
                await scope.ServiceProvider.GetRequiredService<IPrizeClaimService>()
                    .PayAsync(winnerId, claimId, new PayPrizeRequest($"pay-{i}", null));
            }
            catch (DomainException) { }
        })));

        (await BalanceAsync(winnerId)).Should().Be(before - due);
    }

    // ------------------------------------------------------------------ shopping credit expiry

    [Fact]
    public async Task Expiring_an_old_credit_never_takes_newer_credit()
    {
        Guid userId;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await TestData.CreateUserAsync(db, "Shopper")).Id;
            var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();

            // 100 that expires (already past its date), all of it spent, then a fresh 50.
            await wallets.CreditAsync(new WalletMovement
            {
                UserId = userId, WalletType = WalletType.Shopping, Amount = 100m,
                TransactionType = WalletTransactionType.ShoppingCredit, ExpiresAt = DateTime.UtcNow.AddDays(-1),
                IdempotencyKey = "old-credit"
            });
            await wallets.DebitAsync(new WalletMovement
            {
                UserId = userId, WalletType = WalletType.Shopping, Amount = 100m,
                TransactionType = WalletTransactionType.StorePurchase, IdempotencyKey = "spent-it"
            });
            await Task.Delay(20);
            await wallets.CreditAsync(new WalletMovement
            {
                UserId = userId, WalletType = WalletType.Shopping, Amount = 50m,
                TransactionType = WalletTransactionType.ShoppingCredit, ExpiresAt = DateTime.UtcNow.AddYears(1),
                IdempotencyKey = "new-credit"
            });
        }

        await using (var scope = _fixture.CreateScope())
        {
            await ShoppingCreditExpiryWorker.ExpireDueAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                scope.ServiceProvider.GetRequiredService<IWalletService>(),
                DateTime.UtcNow, default);
        }

        (await BalanceAsync(userId, WalletType.Shopping)).Should().Be(50m, "the old credit was already spent; the new one has not expired");
    }

    private sealed class TestEnvironment(string name) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = name;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
    }
}
