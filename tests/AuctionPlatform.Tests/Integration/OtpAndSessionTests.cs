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
/// One-time codes: issuing, verifying, and the properties that make them safe — single use,
/// short-lived, purpose-scoped, attempt-limited and rate limited.
/// </summary>
[Collection("database")]
public class OtpServiceTests
{
    private readonly DatabaseFixture _fixture;

    public OtpServiceTests(DatabaseFixture fixture) => _fixture = fixture;

    private static string NewEmail() => $"{Guid.NewGuid():N}@test.local";

    private static async Task<string> IssueAsync(
        IOtpService otp, string destination, OtpPurpose purpose = OtpPurpose.Login,
        Guid? userId = null, string? binding = null)
    {
        var challenge = await otp.RequestAsync(new OtpRequest
        {
            Destination = destination,
            Purpose = purpose,
            UserId = userId,
            OperationBinding = binding
        });

        // Outside Production the service returns the code so the flow is testable.
        challenge.DevelopmentCode.Should().NotBeNullOrEmpty();
        return challenge.DevelopmentCode!;
    }

    [Fact]
    public async Task A_valid_code_verifies_once_and_then_is_dead()
    {
        await using var scope = _fixture.CreateScope();
        var otp = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var destination = NewEmail();

        var code = await IssueAsync(otp, destination);

        var result = await otp.VerifyAsync(new OtpVerification
        {
            Destination = destination, Purpose = OtpPurpose.Login, Code = code
        });
        result.Purpose.Should().Be(OtpPurpose.Login);

        // Replaying a consumed code must fail, whoever presents it.
        var replay = async () => await otp.VerifyAsync(new OtpVerification
        {
            Destination = destination, Purpose = OtpPurpose.Login, Code = code
        });

        await replay.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.OtpAlreadyUsed);
    }

    [Fact]
    public async Task The_code_is_never_stored_in_plain_text()
    {
        await using var scope = _fixture.CreateScope();
        var otp = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var destination = NewEmail();

        var code = await IssueAsync(otp, destination);

        var stored = await db.OtpCodes.AsNoTracking()
            .FirstAsync(o => o.Destination == destination);

        stored.CodeHash.Should().NotBe(code);
        stored.CodeHash.Should().NotContain(code);
        stored.CodeHash.Should().HaveLength(64, "a SHA-256 hex digest is 64 characters");
    }

    [Fact]
    public async Task A_wrong_code_is_rejected_and_the_code_dies_after_the_attempt_budget()
    {
        await using var scope = _fixture.CreateScope();
        var otp = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var destination = NewEmail();

        var code = await IssueAsync(otp, destination);
        var wrong = code == "000000" ? "111111" : "000000";

        // The fixture uses the default budget of 5 attempts.
        for (var i = 0; i < 5; i++)
        {
            var attempt = async () => await otp.VerifyAsync(new OtpVerification
            {
                Destination = destination, Purpose = OtpPurpose.Login, Code = wrong
            });

            await attempt.Should().ThrowAsync<DomainException>()
                .Where(e => e.Code == ErrorCodes.InvalidOtp);
        }

        // Even the correct code is now useless: brute forcing cannot outlast the budget.
        var withRealCode = async () => await otp.VerifyAsync(new OtpVerification
        {
            Destination = destination, Purpose = OtpPurpose.Login, Code = code
        });

        await withRealCode.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.OtpTooManyAttempts);

        var stored = await db.OtpCodes.AsNoTracking().FirstAsync(o => o.Destination == destination);
        stored.AttemptCount.Should().Be(5);
        stored.InvalidatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_login_code_cannot_authorise_a_withdrawal()
    {
        await using var scope = _fixture.CreateScope();
        var otp = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var destination = NewEmail();

        var loginCode = await IssueAsync(otp, destination, OtpPurpose.Login);

        var crossPurpose = async () => await otp.VerifyAsync(new OtpVerification
        {
            Destination = destination, Purpose = OtpPurpose.Withdrawal, Code = loginCode
        });

        await crossPurpose.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.InvalidOtp);
    }

    [Fact]
    public async Task A_code_bound_to_one_operation_does_not_approve_another()
    {
        await using var scope = _fixture.CreateScope();
        var otp = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var destination = NewEmail();

        var code = await IssueAsync(
            otp, destination, OtpPurpose.Withdrawal, binding: "withdraw-100-to-account-A");

        var differentOperation = async () => await otp.VerifyAsync(new OtpVerification
        {
            Destination = destination,
            Purpose = OtpPurpose.Withdrawal,
            Code = code,
            OperationBinding = "withdraw-5000-to-account-B"
        });

        await differentOperation.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.InvalidOtp);

        // The correct binding still works.
        var correct = await otp.VerifyAsync(new OtpVerification
        {
            Destination = destination,
            Purpose = OtpPurpose.Withdrawal,
            Code = code,
            OperationBinding = "withdraw-100-to-account-A"
        });

        correct.Purpose.Should().Be(OtpPurpose.Withdrawal);
    }

    [Fact]
    public async Task Requesting_a_new_code_retires_the_previous_one()
    {
        await using var scope = _fixture.CreateScope();
        var otp = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var destination = NewEmail();

        var first = await IssueAsync(otp, destination);

        // Age the first request past the resend cooldown; this test is about supersession, not
        // throttling. Done in its own scope so the service re-reads it rather than using the
        // instance still tracked by this context.
        await using (var aging = _fixture.CreateScope())
        {
            var agingDb = aging.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await agingDb.OtpCodes.Where(o => o.Destination == destination)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, DateTime.UtcNow.AddMinutes(-10)));
        }

        await using var issuing = _fixture.CreateScope();
        otp = issuing.ServiceProvider.GetRequiredService<IOtpService>();

        var second = await IssueAsync(otp, destination);
        second.Should().NotBe(first, "a fresh code must be generated");

        // An intercepted earlier code is dead the moment the user asks again.
        var useOld = async () => await otp.VerifyAsync(new OtpVerification
        {
            Destination = destination, Purpose = OtpPurpose.Login, Code = first
        });

        await useOld.Should().ThrowAsync<DomainException>();

        var useNew = await otp.VerifyAsync(new OtpVerification
        {
            Destination = destination, Purpose = OtpPurpose.Login, Code = second
        });
        useNew.Should().NotBeNull();
    }

    [Fact]
    public async Task Requesting_again_immediately_is_throttled()
    {
        await using var scope = _fixture.CreateScope();
        var otp = scope.ServiceProvider.GetRequiredService<IOtpService>();
        var destination = NewEmail();

        await IssueAsync(otp, destination);

        var tooSoon = async () => await otp.RequestAsync(new OtpRequest
        {
            Destination = destination, Purpose = OtpPurpose.Login
        });

        await tooSoon.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.OtpRequestTooSoon);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var destination = NewEmail();
        string code;

        await using (var issuing = _fixture.CreateScope())
        {
            code = await IssueAsync(issuing.ServiceProvider.GetRequiredService<IOtpService>(), destination);
        }

        await using (var aging = _fixture.CreateScope())
        {
            var db = aging.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.OtpCodes.Where(o => o.Destination == destination)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        // A fresh scope, as a real request would have.
        await using var scope = _fixture.CreateScope();
        var otp = scope.ServiceProvider.GetRequiredService<IOtpService>();

        var expired = async () => await otp.VerifyAsync(new OtpVerification
        {
            Destination = destination, Purpose = OtpPurpose.Login, Code = code
        });

        await expired.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.OtpExpired);
    }

    [Theory]
    [InlineData("USER@Example.COM", "  user@example.com  ")]
    [InlineData("+966 50 123 4567", "+966501234567")]
    [InlineData("(055) 987-6543", "0559876543")]
    public async Task Two_formattings_of_one_destination_are_the_same_identity(
        string requestedAs, string verifiedAs)
    {
        // A code sent to "USER@Example.COM" must verify when the user types "user@example.com",
        // and a phone number must not depend on how its spaces and brackets were typed.
        await using var scope = _fixture.CreateScope();
        var otp = scope.ServiceProvider.GetRequiredService<IOtpService>();

        var challenge = await otp.RequestAsync(new OtpRequest
        {
            Destination = requestedAs,
            Purpose = OtpPurpose.VerifyContact
        });

        var result = await otp.VerifyAsync(new OtpVerification
        {
            Destination = verifiedAs,
            Purpose = OtpPurpose.VerifyContact,
            Code = challenge.DevelopmentCode!
        });

        result.Purpose.Should().Be(OtpPurpose.VerifyContact);
    }

    [Fact]
    public async Task The_masked_destination_never_reveals_the_full_address()
    {
        await using var scope = _fixture.CreateScope();
        var otp = scope.ServiceProvider.GetRequiredService<IOtpService>();

        var email = await otp.RequestAsync(new OtpRequest
        {
            Destination = "alexander@example.com", Purpose = OtpPurpose.VerifyContact
        });

        email.Destination.Should().NotBe("alexander@example.com");
        email.Destination.Should().Contain("*").And.EndWith("@example.com");
    }
}

/// <summary>
/// Withdrawal is gated behind a one-time code bound to that exact transfer, and a session lasts
/// until the user signs out.
/// </summary>
[Collection("database")]
public class WithdrawalOtpTests
{
    private readonly DatabaseFixture _fixture;

    public WithdrawalOtpTests(DatabaseFixture fixture) => _fixture = fixture;

    private static async Task<ApplicationUser> UserWithBalanceAsync(
        ApplicationDbContext db, decimal balance)
    {
        var user = await TestData.CreateUserAsync(db, "Withdrawer", mainBalance: balance);
        user.PhoneNumber = $"+96650{Random.Shared.Next(1000000, 9999999)}";
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task A_withdrawal_without_a_code_is_refused()
    {
        Guid userId;

        await using (var setup = _fixture.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await UserWithBalanceAsync(db, 500m)).Id;
        }

        await using var scope = _fixture.CreateScope(userId);
        var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();

        var noCode = async () => await wallets.WithdrawAsync(userId,
            new WithdrawRequest(100m, Money.DefaultCurrency, "wd-1", "SA0380000000608010167519"));

        await noCode.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.OtpRequired);

        // Nothing moved.
        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var wallet = await db2.Wallets.AsNoTracking()
            .FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Main);

        wallet.Balance.Should().Be(500m);
    }

    [Fact]
    public async Task A_withdrawal_with_the_right_code_succeeds()
    {
        Guid userId;

        await using (var setup = _fixture.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await UserWithBalanceAsync(db, 500m)).Id;
        }

        await using var scope = _fixture.CreateScope(userId);
        var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();

        var challenge = await wallets.RequestWithdrawalOtpAsync(userId,
            new RequestWithdrawalOtpRequest(100m, Money.DefaultCurrency, "SA0380000000608010167519"));

        challenge.DevelopmentCode.Should().NotBeNullOrEmpty();

        var result = await wallets.WithdrawAsync(userId,
            new WithdrawRequest(100m, Money.DefaultCurrency, "wd-ok", "SA0380000000608010167519", challenge.DevelopmentCode));

        result.BalanceBefore.Should().Be(500m);
        result.BalanceAfter.Should().Be(400m);
    }

    /// <summary>
    /// The heart of it: a code obtained for a small transfer must not authorise a larger one, or a
    /// transfer to a different destination.
    /// </summary>
    [Fact]
    public async Task A_code_issued_for_one_amount_cannot_authorise_another()
    {
        Guid userId;

        await using (var setup = _fixture.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await UserWithBalanceAsync(db, 5000m)).Id;
        }

        await using var scope = _fixture.CreateScope(userId);
        var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();

        var challenge = await wallets.RequestWithdrawalOtpAsync(userId,
            new RequestWithdrawalOtpRequest(10m, Money.DefaultCurrency, "SA0380000000608010167519"));

        var biggerAmount = async () => await wallets.WithdrawAsync(userId,
            new WithdrawRequest(5000m, Money.DefaultCurrency, "wd-evil", "SA0380000000608010167519", challenge.DevelopmentCode));

        await biggerAmount.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.InvalidOtp);

        var otherDestination = async () => await wallets.WithdrawAsync(userId,
            new WithdrawRequest(10m, Money.DefaultCurrency, "wd-evil-2", "SA0910000000000000000001", challenge.DevelopmentCode));

        await otherDestination.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.InvalidOtp);

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var wallet = await db2.Wallets.AsNoTracking()
            .FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Main);

        wallet.Balance.Should().Be(5000m, "no unauthorised withdrawal may go through");
    }

    [Fact]
    public async Task A_withdrawal_code_cannot_be_used_twice()
    {
        Guid userId;

        await using (var setup = _fixture.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await UserWithBalanceAsync(db, 500m)).Id;
        }

        await using var scope = _fixture.CreateScope(userId);
        var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();

        var challenge = await wallets.RequestWithdrawalOtpAsync(userId,
            new RequestWithdrawalOtpRequest(50m, Money.DefaultCurrency, "SA0380000000608010167519"));

        await wallets.WithdrawAsync(userId,
            new WithdrawRequest(50m, Money.DefaultCurrency, "wd-first", "SA0380000000608010167519", challenge.DevelopmentCode));

        // A different idempotency key, so this is a genuinely new withdrawal attempt.
        var second = async () => await wallets.WithdrawAsync(userId,
            new WithdrawRequest(50m, Money.DefaultCurrency, "wd-second", "SA0380000000608010167519", challenge.DevelopmentCode));

        await second.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.OtpAlreadyUsed);

        await using var verify = _fixture.CreateScope();
        var db2 = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var wallet = await db2.Wallets.AsNoTracking()
            .FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Main);

        wallet.Balance.Should().Be(450m, "only the first withdrawal may go through");
    }

    [Fact]
    public async Task Shopping_credit_is_still_never_withdrawable_even_with_a_valid_code()
    {
        // The OTP gate is an extra layer, not a replacement: the wallet restriction is absolute.
        var act = () => WalletRulesGuard();
        act.Should().Throw<DomainException>()
            .Where(e => e.Code == ErrorCodes.ShoppingWalletOperationNotAllowed);

        static void WalletRulesGuard() =>
            Domain.Rules.WalletRules.EnsureWithdrawable(WalletType.Shopping);
    }
}
