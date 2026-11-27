using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>Saving a phone's push token, which the app often sends twice at the same moment.</summary>
[Collection("database")]
public class DeviceTokenTests
{
    private readonly DatabaseFixture _fixture;

    public DeviceTokenTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task<Guid> NewUserAsync()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var email = $"device-{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FullName = "Device Test",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task RegisterAsync(Guid userId, string token, string? platform)
    {
        await using var scope = _fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<INotificationService>()
            .RegisterDeviceAsync(userId, token, platform);
    }

    private async Task<List<UserDeviceToken>> RowsAsync(string token)
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .UserDeviceTokens.AsNoTracking().Where(t => t.Token == token).ToListAsync();
    }

    [Fact]
    public async Task The_same_token_sent_many_times_at_once_is_saved_once_without_an_error()
    {
        var user = await NewUserAsync();
        var token = $"fcm-{Guid.NewGuid():N}";

        var act = () => Task.WhenAll(Enumerable.Range(0, 10).Select(_ => RegisterAsync(user, token, "android")));

        await act.Should().NotThrowAsync();
        var rows = await RowsAsync(token);
        rows.Should().ContainSingle().Which.UserId.Should().Be(user);
    }

    [Fact]
    public async Task A_token_follows_the_account_that_signed_in_last_and_keeps_its_platform()
    {
        var first = await NewUserAsync();
        var second = await NewUserAsync();
        var token = $"fcm-{Guid.NewGuid():N}";

        await RegisterAsync(first, token, "ios");
        var before = (await RowsAsync(token)).Single();
        await RegisterAsync(second, token, null);

        var row = (await RowsAsync(token)).Single();
        row.Id.Should().Be(before.Id);
        row.UserId.Should().Be(second);
        row.Platform.Should().Be("ios");
        row.LastSeenAt.Should().BeOnOrAfter(before.LastSeenAt);
    }
}
