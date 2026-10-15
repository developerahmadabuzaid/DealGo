using AuctionPlatform.Application.Identity;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// Signing in with a phone number is also how an account is created: a number with no account
/// gets a real code, and verifying it creates the account.
/// </summary>
[Collection("database")]
public class PhoneSignInTests
{
    private readonly DatabaseFixture _fixture;

    public PhoneSignInTests(DatabaseFixture fixture) => _fixture = fixture;

    private static string NewPhone() =>
        "+9665" + Random.Shared.NextInt64(10_000_000, 99_999_999);

    private async Task<AuthResultDto> SignInAsync(string phone)
    {
        await using var scope = _fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedEssentialsAsync();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var challenge = await auth.RequestLoginOtpAsync(new RequestLoginOtpRequest(phone, "ar"));
        challenge.DevelopmentCode.Should().NotBeNullOrEmpty("a new phone number must get a real code");

        return await auth.VerifyLoginOtpAsync(new VerifyLoginOtpRequest(phone, challenge.DevelopmentCode!));
    }

    [Fact]
    public async Task A_new_phone_number_creates_an_account_on_verification()
    {
        var phone = NewPhone();

        var result = await SignInAsync(phone);

        result.AccessToken.Should().NotBeNullOrEmpty();
        result.User.PhoneNumber.Should().Be(phone);
        result.User.FullName.Should().BeEmpty("the app asks for the name after sign-in");
        result.User.Roles.Should().Contain(Roles.User);

        await using var scope = _fixture.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(result.User.Id.ToString());
        user!.PhoneNumberConfirmed.Should().BeTrue();
        user.Email.Should().BeNull();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.Wallets.CountAsync(w => w.UserId == user.Id)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Signing_in_again_with_the_same_number_reuses_the_account()
    {
        var phone = NewPhone();

        var first = await SignInAsync(phone);

        // Wait out the resend cooldown by retiring the earlier codes' timestamps.
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.OtpCodes
                .Where(o => o.Destination == phone)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, o => o.CreatedAt.AddHours(-2)));
        }

        var second = await SignInAsync(phone);

        second.User.Id.Should().Be(first.User.Id);
    }

    [Fact]
    public async Task A_local_and_an_international_spelling_reach_the_same_account()
    {
        var national = NewPhone()["+966".Length..];

        var first = await SignInAsync("0" + national);

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.OtpCodes
                .Where(o => o.Destination == "+966" + national)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, o => o.CreatedAt.AddHours(-2)));
        }

        var second = await SignInAsync("+966 " + national);

        second.User.Id.Should().Be(first.User.Id);
        second.User.PhoneNumber.Should().Be("+966" + national, "numbers are stored in one canonical form");
    }

    [Theory]
    [InlineData("+962791234567")]
    [InlineData("0112345678")]
    [InlineData("1234567")]
    public async Task A_number_that_is_not_a_Saudi_mobile_is_refused(string phone)
    {
        await using var scope = _fixture.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var request = async () => await auth.RequestLoginOtpAsync(new RequestLoginOtpRequest(phone, "ar"));

        await request.Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == ErrorCodes.PhoneNumberInvalid);
    }

    [Fact]
    public async Task An_unknown_email_still_gets_no_real_code()
    {
        await using var scope = _fixture.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var challenge = await auth.RequestLoginOtpAsync(
            new RequestLoginOtpRequest($"{Guid.NewGuid():N}@test.local", "ar"));

        challenge.DevelopmentCode.Should().BeNull();
    }
}
