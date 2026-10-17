using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The first super administrator on a fresh Production deployment.
///
/// One test, run as a sequence, because the rule under test is about order: the setting works
/// exactly once, while nobody holds the role, and is inert from then on. Split into separate
/// tests sharing one database, the outcome would depend on which ran first.
/// </summary>
[Collection("database")]
public class BootstrapAdminTests
{
    private readonly DatabaseFixture _fixture;

    public BootstrapAdminTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_bootstrap_account_is_promoted_once_and_never_again()
    {
        await using var scope = _fixture.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        await seeder.SeedEssentialsAsync();

        // Nothing configured, and an address nobody has registered: no-one is promoted.
        (await seeder.PromoteBootstrapSuperAdminAsync(null)).Should().BeFalse();
        (await seeder.PromoteBootstrapSuperAdminAsync("   ")).Should().BeFalse();
        (await seeder.PromoteBootstrapSuperAdminAsync($"{Guid.NewGuid():N}@nobody.local")).Should().BeFalse();

        // Not a Saudi mobile number, and a valid one nobody holds: still no-one.
        (await seeder.PromoteBootstrapSuperAdminAsync(null, "12345")).Should().BeFalse();
        (await seeder.PromoteBootstrapSuperAdminAsync(null, "0599999990")).Should().BeFalse();

        // People sign up in the app with their phone, so the first administrator is usually named
        // by number — written here the way a person types it, not in the stored form.
        var first = await RegisterAsync(users);
        first.PhoneNumber = "+9665" + Random.Shared.NextInt64(10_000_000, 99_999_999);
        (await users.UpdateAsync(first)).Succeeded.Should().BeTrue();

        var typed = "0" + first.PhoneNumber[4..];
        (await seeder.PromoteBootstrapSuperAdminAsync(null, typed)).Should().BeTrue();
        (await users.IsInRoleAsync(first, Roles.SuperAdmin)).Should().BeTrue();

        // A super administrator now exists, so pointing the setting at somebody else does nothing.
        // This is what stops the setting being used later to take over the platform.
        var second = await RegisterAsync(users);
        (await seeder.PromoteBootstrapSuperAdminAsync(second.Email)).Should().BeFalse();
        (await users.IsInRoleAsync(second, Roles.SuperAdmin)).Should().BeFalse();

        (await users.GetUsersInRoleAsync(Roles.SuperAdmin)).Should().ContainSingle()
            .Which.Id.Should().Be(first.Id);
    }

    /// <summary>An account created the way sign-up creates one, with no role beyond the default.</summary>
    private static async Task<ApplicationUser> RegisterAsync(UserManager<ApplicationUser> users)
    {
        var email = $"{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = "Bootstrap Candidate",
            IsActive = true
        };

        var result = await users.CreateAsync(user);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
    }
}
