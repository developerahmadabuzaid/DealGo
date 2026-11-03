using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using AuctionPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>The console's list of every deposit and withdrawal, and the full detail of each.</summary>
[Collection("database")]
public class MoneyOperationsTests
{
    private const string Iban = "SA0380000000608010167519";

    private readonly DatabaseFixture _fixture;

    public MoneyOperationsTests(DatabaseFixture fixture) => _fixture = fixture;

    private IMoneyOperationsService Operations(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMoneyOperationsService>();

    [Fact]
    public async Task A_paid_top_up_shows_its_ledger_line_callback_and_card()
    {
        Guid userId;
        await using (var setup = _fixture.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await TestData.CreateUserAsync(db, "OpsPayer", mainBalance: 0m)).Id;
        }

        Guid paymentId;
        await using (var scope = _fixture.CreateScope())
        {
            paymentId = (await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>()
                .StartAsync(userId, 120m, "https://site.test")).PaymentId;
        }

        var (body, hmac) = WalletTopUpTests.Callback(paymentId, 12000, transactionId: 880011);
        await using (var scope = _fixture.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>().HandleCallbackAsync(body, hmac))
                .Accepted.Should().BeTrue();
        }

        await using var read = _fixture.CreateScope();

        var page = await Operations(read).ListAsync(new MoneyOperationQuery
        {
            Type = PaymentType.Deposit, Search = userId.ToString(), PageSize = 50
        });
        var row = page.Page.Items.Should().ContainSingle().Subject;
        row.Id.Should().Be(paymentId);
        row.Status.Should().Be(PaymentStatus.Completed);
        page.Totals.DepositedCompleted.Should().Be(120m);
        page.Totals.DepositsCompleted.Should().Be(1);

        var detail = await Operations(read).GetAsync(paymentId);
        detail.Amount.Should().Be(120m);
        detail.GatewayTransactionId.Should().Be("880011");
        detail.GatewayOrderId.Should().NotBeNull();
        detail.PaymentMethod.Should().Be("card");
        detail.CardBrand.Should().Be("MasterCard");
        detail.CardLast4.Should().Be("2346");
        detail.MainBalance.Should().Be(120m);

        var credit = detail.Ledger.Should().ContainSingle().Subject;
        credit.Amount.Should().Be(120m);
        credit.BalanceBefore.Should().Be(0m);
        credit.BalanceAfter.Should().Be(120m);

        var callback = detail.Callbacks.Should().ContainSingle().Subject;
        callback.SignatureValid.Should().BeTrue();
        callback.Processed.Should().BeTrue();
        callback.Success.Should().BeTrue();
    }

    [Fact]
    public async Task A_refused_withdrawal_shows_its_iban_both_ledger_lines_and_who_refused_it()
    {
        Guid userId;
        await using (var setup = _fixture.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await TestData.CreateUserAsync(db, "OpsWithdraw", mainBalance: 300m);
            user.PhoneNumber = $"+96650{Random.Shared.Next(1000000, 9999999)}";
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        Guid paymentId;
        await using (var scope = _fixture.CreateScope(userId))
        {
            var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();
            var challenge = await wallets.RequestWithdrawalOtpAsync(userId,
                new RequestWithdrawalOtpRequest(80m, Money.DefaultCurrency, Iban));
            await wallets.WithdrawAsync(userId,
                new WithdrawRequest(80m, Money.DefaultCurrency, $"wd-{Guid.NewGuid():N}", Iban, challenge.DevelopmentCode));
            paymentId = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Payments
                .Where(p => p.UserId == userId && p.PaymentType == PaymentType.Withdrawal)
                .Select(p => p.Id).SingleAsync();
        }

        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAdminWithdrawalService>()
                .RejectAsync(paymentId, new RejectWithdrawalRequest("Name does not match"));
        }

        await using var read = _fixture.CreateScope();

        var page = await Operations(read).ListAsync(new MoneyOperationQuery { Search = userId.ToString() });
        page.Page.Items.Should().ContainSingle(r => r.Id == paymentId && r.Reference == Iban);

        var detail = await Operations(read).GetAsync(paymentId);
        detail.Type.Should().Be(PaymentType.Withdrawal);
        detail.Status.Should().Be(PaymentStatus.Cancelled);
        detail.Iban.Should().Be(Iban);
        detail.FailureReason.Should().Be("Name does not match");
        detail.MainBalance.Should().Be(300m);

        detail.Ledger.Select(l => l.Amount).Should().Equal(-80m, 80m);
        detail.History.Should().ContainSingle(h => h.Action == AuditActionType.WithdrawalRejected);
        detail.Callbacks.Should().BeEmpty();
    }

    [Fact]
    public async Task Other_payments_are_not_operations()
    {
        await using var scope = _fixture.CreateScope();
        var act = () => Operations(scope).GetAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.NotFound);
    }
}
