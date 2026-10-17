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
/// The admin console signs staff in with their phone number, through the same endpoints as the app
/// but with StaffOnly set. A number that is not an active staff account must get nothing: no code,
/// no account, and no hint that the number exists.
/// </summary>
[Collection("database")]
public class StaffPhoneSignInTests
{
    private readonly DatabaseFixture _fixture;

    public StaffPhoneSignInTests(DatabaseFixture fixture) => _fixture = fixture;

    private static string NewPhone() =>
        "+9665" + Random.Shared.NextInt64(10_000_000, 99_999_999);

    private async Task<ApplicationUser> CreateAccountAsync(string phone, string role)
    {
        await using var scope = _fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedEssentialsAsync();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await TestData.CreateUserAsync(db, "Staff Member");
        user.PhoneNumber = phone;
        await db.SaveChangesAsync();
        await users.AddToRoleAsync(user, role);
        return user;
    }

    [Fact]
    public async Task A_staff_member_signs_in_with_their_number()
    {
        var phone = NewPhone();
        var staff = await CreateAccountAsync(phone, Roles.StoreManager);

        await using var scope = _fixture.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        // Written the way people type it; the server reduces it to the stored form.
        var typed = "0" + phone[4..];
        var challenge = await auth.RequestLoginOtpAsync(new RequestLoginOtpRequest(typed, "ar", StaffOnly: true));
        challenge.DevelopmentCode.Should().NotBeNullOrEmpty();

        var result = await auth.VerifyLoginOtpAsync(
            new VerifyLoginOtpRequest(typed, challenge.DevelopmentCode!, StaffOnly: true));

        result.User.Id.Should().Be(staff.Id);
        result.User.Roles.Should().Contain(Roles.StoreManager);
    }

    [Fact]
    public async Task An_unknown_number_gets_a_decoy_and_no_account()
    {
        var phone = NewPhone();

        await using var scope = _fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedEssentialsAsync();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var challenge = await auth.RequestLoginOtpAsync(new RequestLoginOtpRequest(phone, "ar", StaffOnly: true));

        challenge.DevelopmentCode.Should().BeNull("no code is issued, so none can be echoed");
        challenge.Destination.Should().NotContain(phone[^7..], "the reply looks like any other");
        (await db.OtpCodes.AnyAsync(o => o.Destination == phone)).Should().BeFalse();

        var verify = () => auth.VerifyLoginOtpAsync(new VerifyLoginOtpRequest(phone, "123456", StaffOnly: true));
        await verify.Should().ThrowAsync<DomainException>();
        (await db.Users.AnyAsync(u => u.PhoneNumber == phone)).Should().BeFalse();
    }

    [Fact]
    public async Task A_customer_number_gets_a_decoy_too()
    {
        var phone = NewPhone();
        await CreateAccountAsync(phone, Roles.User);

        await using var scope = _fixture.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var challenge = await auth.RequestLoginOtpAsync(new RequestLoginOtpRequest(phone, "ar", StaffOnly: true));
        challenge.DevelopmentCode.Should().BeNull();
    }

    [Fact]
    public async Task A_code_issued_to_the_app_does_not_open_the_console()
    {
        var phone = NewPhone();
        await CreateAccountAsync(phone, Roles.User);

        await using var scope = _fixture.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        // A genuine code from the app's sign-in, presented to the console.
        var challenge = await auth.RequestLoginOtpAsync(new RequestLoginOtpRequest(phone, "ar"));

        var verify = () => auth.VerifyLoginOtpAsync(
            new VerifyLoginOtpRequest(phone, challenge.DevelopmentCode!, StaffOnly: true));

        (await verify.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.Forbidden);
    }
}
