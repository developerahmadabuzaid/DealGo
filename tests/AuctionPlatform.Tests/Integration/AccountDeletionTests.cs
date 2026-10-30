using AuctionPlatform.Application.Identity;
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
/// A user can delete their own account, confirmed with a code sent to their phone. Personal details
/// go; the financial record stays.
/// </summary>
[Collection("database")]
public class AccountDeletionTests
{
    private readonly DatabaseFixture _fixture;

    public AccountDeletionTests(DatabaseFixture fixture) => _fixture = fixture;

    private static string NewPhone() =>
        "+9665" + Random.Shared.NextInt64(10_000_000, 99_999_999);

    private async Task<AuthResultDto> SignInAsync(string phone)
    {
        await using var scope = _fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedEssentialsAsync();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var challenge = await auth.RequestLoginOtpAsync(new RequestLoginOtpRequest(phone, "ar"));
        return await auth.VerifyLoginOtpAsync(new VerifyLoginOtpRequest(phone, challenge.DevelopmentCode!));
    }

    private async Task<string> RequestCodeAsync(Guid userId)
    {
        await using var scope = _fixture.CreateScope(userId);
        var challenge = await scope.ServiceProvider.GetRequiredService<IAccountDeletionService>().RequestAsync(userId);
        challenge.DevelopmentCode.Should().NotBeNullOrEmpty();
        return challenge.DevelopmentCode!;
    }

    private async Task DeleteAsync(Guid userId, string code)
    {
        await using var scope = _fixture.CreateScope(userId);
        await scope.ServiceProvider.GetRequiredService<IAccountDeletionService>()
            .DeleteAsync(userId, new DeleteAccountRequest(code));
    }

    private async Task MoveAsync(Guid userId, decimal amount, string key)
    {
        await using var scope = _fixture.CreateScope();
        var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();
        var movement = new WalletMovement
        {
            UserId = userId, WalletType = WalletType.Main, Amount = Math.Abs(amount),
            Currency = Money.DefaultCurrency, TransactionType = WalletTransactionType.Adjustment,
            ReferenceType = "Test", Description = "Test", IdempotencyKey = key
        };
        if (amount > 0) await wallets.CreditAsync(movement);
        else await wallets.DebitAsync(movement);
    }

    [Fact]
    public async Task A_confirmed_deletion_removes_personal_details_and_ends_every_session()
    {
        var phone = NewPhone();
        var signedIn = await SignInAsync(phone);
        var userId = signedIn.User.Id;

        // Money that came and went: the ledger has rows the database will not let anyone delete.
        await MoveAsync(userId, 50m, "in");
        await MoveAsync(userId, -50m, "out");

        await DeleteAsync(userId, await RequestCodeAsync(userId));

        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);

        user.IsActive.Should().BeFalse();
        user.PhoneNumber.Should().BeNull();
        user.Email.Should().BeNull();
        user.FullName.Should().BeEmpty();
        user.PasswordHash.Should().BeNull();
        user.UserName.Should().StartWith("deleted-");

        (await db.RefreshTokens.AnyAsync(t => t.UserId == userId && t.RevokedAt == null))
            .Should().BeFalse("every session ends");
        (await db.WalletTransactions.CountAsync(t => t.UserId == userId))
            .Should().Be(2, "the financial record is kept");
        (await db.AuditLogs.AnyAsync(a => a.Action == AuditActionType.AccountDeleted && a.EntityId == userId))
            .Should().BeTrue();

        var refresh = () => scope.ServiceProvider.GetRequiredService<IAuthService>()
            .RefreshAsync(new RefreshRequest(signedIn.RefreshToken));
        await refresh.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Signing_in_again_with_the_same_number_starts_a_new_account()
    {
        var phone = NewPhone();
        var first = await SignInAsync(phone);
        await DeleteAsync(first.User.Id, await RequestCodeAsync(first.User.Id));

        // Past the login code's resend cooldown.
        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().OtpCodes
                .Where(o => o.Destination == phone)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, o => o.CreatedAt.AddHours(-2)));
        }

        var second = await SignInAsync(phone);

        second.User.Id.Should().NotBe(first.User.Id);
    }

    [Fact]
    public async Task A_wrong_code_deletes_nothing()
    {
        var userId = (await SignInAsync(NewPhone())).User.Id;
        var code = await RequestCodeAsync(userId);
        var wrong = code == "000000" ? "111111" : "000000";

        var act = () => DeleteAsync(userId, wrong);
        await act.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.InvalidOtp);

        await using var scope = _fixture.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        user.IsActive.Should().BeTrue();
        user.PhoneNumber.Should().NotBeNull();
    }

    [Fact]
    public async Task A_sign_in_code_cannot_delete_the_account()
    {
        var phone = NewPhone();
        var userId = (await SignInAsync(phone)).User.Id;

        string loginCode;
        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().OtpCodes
                .Where(o => o.Destination == phone)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, o => o.CreatedAt.AddHours(-2)));
            loginCode = (await scope.ServiceProvider.GetRequiredService<IAuthService>()
                .RequestLoginOtpAsync(new RequestLoginOtpRequest(phone, "ar"))).DevelopmentCode!;
        }

        var act = () => DeleteAsync(userId, loginCode);

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task An_account_with_a_withdrawable_balance_is_not_deleted()
    {
        var userId = (await SignInAsync(NewPhone())).User.Id;
        await MoveAsync(userId, 25m, "keep");

        var act = () => RequestCodeAsync(userId);

        await act.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.AccountHasBalance);
    }

    [Fact]
    public async Task An_account_with_a_payment_in_progress_is_not_deleted()
    {
        var userId = (await SignInAsync(NewPhone())).User.Id;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Payments.Add(new Payment
            {
                UserId = userId, PaymentType = PaymentType.Withdrawal, Amount = 10m,
                Currency = Money.DefaultCurrency, Provider = "SANDBOX", Status = PaymentStatus.Processing,
                IdempotencyKey = $"open-{Guid.NewGuid():N}"
            });
            await db.SaveChangesAsync();
        }

        var act = () => RequestCodeAsync(userId);

        await act.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.AccountHasOpenActivity);
    }
}
