using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// A withdrawal takes the money from the wallet and waits in the admin queue. Staff mark it paid
/// after the bank transfer, or refuse it and the money goes back, each exactly once.
/// </summary>
[Collection("database")]
public class WithdrawalQueueTests
{
    private const string Iban = "SA0380000000608010167519";

    private readonly DatabaseFixture _fixture;

    public WithdrawalQueueTests(DatabaseFixture fixture) => _fixture = fixture;

    /// <summary>A user with 500 in the main wallet who has asked to withdraw 100.</summary>
    private async Task<(Guid UserId, Guid PaymentId)> WithdrawAsync(string iban = Iban)
    {
        Guid userId;
        await using (var setup = _fixture.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await TestData.CreateUserAsync(db, "Queue", mainBalance: 500m);
            user.PhoneNumber = $"+96650{Random.Shared.Next(1000000, 9999999)}";
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        await using var scope = _fixture.CreateScope(userId);
        var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();
        var challenge = await wallets.RequestWithdrawalOtpAsync(userId,
            new RequestWithdrawalOtpRequest(100m, Money.DefaultCurrency, iban));
        await wallets.WithdrawAsync(userId,
            new WithdrawRequest(100m, Money.DefaultCurrency, $"wd-{Guid.NewGuid():N}", iban, challenge.DevelopmentCode));

        var paymentId = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Payments
            .Where(p => p.UserId == userId && p.PaymentType == PaymentType.Withdrawal)
            .Select(p => p.Id).SingleAsync();

        return (userId, paymentId);
    }

    private async Task<decimal> MainBalanceAsync(Guid userId)
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Wallets
            .Where(w => w.UserId == userId && w.WalletType == WalletType.Main)
            .Select(w => w.Balance).SingleAsync();
    }

    private IAdminWithdrawalService Queue(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IAdminWithdrawalService>();

    [Fact]
    public async Task A_withdrawal_waits_in_the_queue_with_its_iban()
    {
        var (userId, paymentId) = await WithdrawAsync("sa03 8000 0000 6080 1016 7519");

        await using var scope = _fixture.CreateScope();
        var page = await Queue(scope).ListAsync(new AdminWithdrawalQuery { Status = PaymentStatus.Processing, PageSize = 100 });

        var row = page.Items.Should().ContainSingle(w => w.Id == paymentId).Subject;
        row.UserId.Should().Be(userId);
        row.Iban.Should().Be(Iban, "spaces and lower case are tidied before it is stored");
        row.Amount.Should().Be(100m);
        (await MainBalanceAsync(userId)).Should().Be(400m, "the money leaves the wallet when the user asks");
    }

    [Fact]
    public async Task A_withdrawal_needs_a_valid_saudi_iban()
    {
        await using var setup = _fixture.CreateScope();
        var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await TestData.CreateUserAsync(db, "BadIban", mainBalance: 500m);
        user.PhoneNumber = $"+96650{Random.Shared.Next(1000000, 9999999)}";
        await db.SaveChangesAsync();

        await using var scope = _fixture.CreateScope(user.Id);
        var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();

        // One digit changed: the check digits no longer match.
        foreach (var bad in new[] { "SA0380000000608010167518", "IBAN123", "", "GB82WEST12345698765432" })
        {
            var act = () => wallets.RequestWithdrawalOtpAsync(user.Id,
                new RequestWithdrawalOtpRequest(100m, Money.DefaultCurrency, bad));
            await act.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.IbanInvalid, bad);
        }
    }

    [Fact]
    public async Task Marking_paid_records_the_bank_reference_and_leaves_the_balance()
    {
        var (userId, paymentId) = await WithdrawAsync();

        await using (var scope = _fixture.CreateScope())
        {
            var row = await Queue(scope).MarkPaidAsync(paymentId, new MarkWithdrawalPaidRequest("TRF-778899"));
            row.Status.Should().Be(PaymentStatus.Completed);
            row.BankReference.Should().Be("TRF-778899");
        }

        (await MainBalanceAsync(userId)).Should().Be(400m);

        await using var check = _fixture.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AuditLogs.AnyAsync(a => a.Action == AuditActionType.WithdrawalPaid && a.EntityId == paymentId))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Refusing_puts_the_amount_back_once()
    {
        var (userId, paymentId) = await WithdrawAsync();

        await using (var scope = _fixture.CreateScope())
        {
            var row = await Queue(scope).RejectAsync(paymentId, new RejectWithdrawalRequest("IBAN name does not match"));
            row.Status.Should().Be(PaymentStatus.Cancelled);
            row.FailureReason.Should().Be("IBAN name does not match");
        }

        (await MainBalanceAsync(userId)).Should().Be(500m);

        await using (var again = _fixture.CreateScope())
        {
            var act = () => Queue(again).RejectAsync(paymentId, new RejectWithdrawalRequest("twice"));
            await act.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.WithdrawalNotPending);
        }

        (await MainBalanceAsync(userId)).Should().Be(500m, "a second refusal must not refund again");
    }

    [Fact]
    public async Task A_paid_withdrawal_cannot_then_be_refused()
    {
        var (userId, paymentId) = await WithdrawAsync();

        await using (var scope = _fixture.CreateScope())
        {
            await Queue(scope).MarkPaidAsync(paymentId, new MarkWithdrawalPaidRequest("TRF-1"));
        }

        await using (var scope = _fixture.CreateScope())
        {
            var act = () => Queue(scope).RejectAsync(paymentId, new RejectWithdrawalRequest("too late"));
            await act.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.WithdrawalNotPending);
        }

        (await MainBalanceAsync(userId)).Should().Be(400m, "money that was transferred is not refunded as well");
    }

    [Fact]
    public async Task Paying_and_refusing_at_the_same_moment_settles_it_once()
    {
        var (userId, paymentId) = await WithdrawAsync();

        async Task<bool> Try(Func<IAdminWithdrawalService, Task> action)
        {
            await using var scope = _fixture.CreateScope();
            try { await action(Queue(scope)); return true; }
            catch (DomainException e) when (e.Code == ErrorCodes.WithdrawalNotPending) { return false; }
        }

        var results = await Task.WhenAll(
            Try(q => q.MarkPaidAsync(paymentId, new MarkWithdrawalPaidRequest("TRF-race"))),
            Try(q => q.RejectAsync(paymentId, new RejectWithdrawalRequest("race"))),
            Try(q => q.RejectAsync(paymentId, new RejectWithdrawalRequest("race 2"))));

        results.Count(r => r).Should().Be(1);

        await using var check = _fixture.CreateScope();
        var payment = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>().Payments
            .AsNoTracking().SingleAsync(p => p.Id == paymentId);

        (await MainBalanceAsync(userId)).Should().Be(payment.Status == PaymentStatus.Completed ? 400m : 500m);
    }

    [Fact]
    public async Task Marking_paid_needs_a_bank_reference()
    {
        var (_, paymentId) = await WithdrawAsync();

        await using var scope = _fixture.CreateScope();
        var act = () => Queue(scope).MarkPaidAsync(paymentId, new MarkWithdrawalPaidRequest("  "));

        await act.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.ValidationFailed);
    }
}
