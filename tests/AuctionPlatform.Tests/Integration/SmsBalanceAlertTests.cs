using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Identity;
using AuctionPlatform.Infrastructure.Persistence;
using AuctionPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>When Tawked has no balance left, staff hear about it from us and not from a customer.</summary>
[Collection("database")]
public class SmsBalanceAlertTests
{
    private readonly DatabaseFixture _fixture;

    public SmsBalanceAlertTests(DatabaseFixture fixture) => _fixture = fixture;

    private static string NewPhone() => "+9665" + Random.Shared.NextInt64(10_000_000, 99_999_999);

    private sealed class Tawked : ITawkedVerifyClient
    {
        public bool OutOfCredit { get; init; }

        public bool IsEnabled => true;

        public Task<TawkedVerification> StartAsync(string phoneE164, string language, string reference,
            string? clientIp, string idempotencyKey, CancellationToken ct = default) =>
            OutOfCredit
                ? throw new TawkedOutOfCreditException("briefly unavailable")
                : Task.FromResult(new TawkedVerification(Guid.NewGuid().ToString(), DateTime.UtcNow.AddMinutes(5)));

        public Task<TawkedCheckOutcome> CheckAsync(string verificationId, string code,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Later(TimeSpan by) : IDateTimeProvider
    {
        public DateTime UtcNow => DateTime.UtcNow + by;
    }

    private Task RequestAsync(AsyncServiceScope scope, bool outOfCredit) =>
        ActivatorUtilities.CreateInstance<OtpService>(scope.ServiceProvider, (ITawkedVerifyClient)new Tawked { OutOfCredit = outOfCredit })
            .RequestAsync(new OtpRequest { Destination = NewPhone(), Purpose = OtpPurpose.Login });

    /// <summary>A code that went out ends whatever alert an earlier test left behind.</summary>
    private async Task StartWithBalanceAsync()
    {
        await using var scope = _fixture.CreateScope();
        await RequestAsync(scope, outOfCredit: false);
    }

    private async Task RefuseAsync()
    {
        await using var scope = _fixture.CreateScope();
        var act = () => RequestAsync(scope, outOfCredit: true);
        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ServiceUnavailable);
    }

    private async Task<DateTime?> EmptySinceAsync()
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISmsBalanceAlert>().GetEmptySinceAsync();
    }

    private async Task<Guid> CreateUserAsync(string role)
    {
        await using var scope = _fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedEssentialsAsync();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await TestData.CreateUserAsync(db, "Someone");
        await users.AddToRoleAsync(user, role);
        return user.Id;
    }

    private async Task<int> AlertsReceivedByAsync(Guid userId)
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Notifications
            .CountAsync(n => n.UserId == userId && n.TitleKey == NotificationKeys.SmsBalanceEmptyTitle);
    }

    [Fact]
    public async Task An_empty_balance_tells_admins_only_and_only_once()
    {
        await StartWithBalanceAsync();
        var admin = await CreateUserAsync(Roles.Admin);
        var customer = await CreateUserAsync(Roles.User);

        await RefuseAsync();
        await RefuseAsync();

        (await AlertsReceivedByAsync(admin)).Should().Be(1);
        (await AlertsReceivedByAsync(customer)).Should().Be(0);
        (await EmptySinceAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task The_alert_repeats_after_some_hours_while_the_balance_stays_empty()
    {
        await StartWithBalanceAsync();
        var admin = await CreateUserAsync(Roles.Admin);
        await RefuseAsync();

        await using var scope = _fixture.CreateScope();
        var later = ActivatorUtilities.CreateInstance<SmsBalanceAlert>(
            scope.ServiceProvider, (IDateTimeProvider)new Later(SmsBalanceAlert.RepeatEvery + TimeSpan.FromMinutes(1)));
        await later.RaiseAsync();

        (await AlertsReceivedByAsync(admin)).Should().Be(2);
    }

    [Fact]
    public async Task The_warning_clears_once_a_code_goes_out_and_can_come_back()
    {
        await StartWithBalanceAsync();
        (await EmptySinceAsync()).Should().BeNull();

        await RefuseAsync();
        (await EmptySinceAsync()).Should().NotBeNull();

        await StartWithBalanceAsync();
        (await EmptySinceAsync()).Should().BeNull();

        var admin = await CreateUserAsync(Roles.Admin);
        await RefuseAsync();
        (await EmptySinceAsync()).Should().NotBeNull();
        (await AlertsReceivedByAsync(admin)).Should().Be(1);
    }
}
