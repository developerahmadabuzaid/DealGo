using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Identity;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// Finding a user by phone in the console, typed the way people type it.
///
/// Numbers are stored as +9665XXXXXXXX. Searching "0500000001" used to find nothing, because the
/// local leading zero is not in the stored form, so an administrator looking someone up concluded
/// they did not exist.
/// </summary>
[Collection("database")]
public class UserSearchTests
{
    private readonly DatabaseFixture _fixture;

    public UserSearchTests(DatabaseFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("0566001122")]
    [InlineData("566001122")]
    [InlineData("+966566001122")]
    [InlineData("966566001122")]
    [InlineData("056 600 1122")]
    public async Task A_user_is_found_by_phone_however_the_number_is_typed(string typed)
    {
        await using var scope = _fixture.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ApplicationDbContext>();

        if (!db.Users.Any(u => u.PhoneNumber == "+966566001122"))
        {
            db.Users.Add(new ApplicationUser
            {
                UserName = $"phone-{Guid.NewGuid():N}",
                PhoneNumber = "+966566001122",
                FullName = string.Empty,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var service = new UserAdminService(
            db,
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IAuditService>(),
            sp.GetRequiredService<IDateTimeProvider>());

        var found = await service.ListAsync(new UserListQuery { Search = typed });

        found.Items.Select(u => u.PhoneNumber).Should().Contain("+966566001122");
    }
}
