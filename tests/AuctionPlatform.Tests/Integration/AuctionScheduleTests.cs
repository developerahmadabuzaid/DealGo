using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// Moving an auction's start time from the console: only to a time still to come.
/// </summary>
[Collection("database")]
public class AuctionScheduleTests
{
    private readonly DatabaseFixture _fixture;

    public AuctionScheduleTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task<Guid> CreateWaitingAuctionAsync(DateTime scheduledStartAt)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var auction = await TestData.CreateLiveAuctionAsync(db);

        auction.Status = AuctionStatus.WaitingForStart;
        auction.ActualStartedAt = null;
        auction.ScheduledStartAt = scheduledStartAt;
        await db.SaveChangesAsync();

        return auction.Id;
    }

    [Fact]
    public async Task A_start_time_in_the_past_is_refused_and_nothing_changes()
    {
        var original = DateTime.UtcNow.AddHours(2);
        var id = await CreateWaitingAuctionAsync(original);

        await using (var scope = _fixture.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();

            var act = () => admin.UpdateScheduleAsync(
                id, new UpdateScheduleRequest(DateTime.UtcNow.AddMinutes(-10), "typo"));

            (await act.Should().ThrowAsync<DomainException>())
                .Which.Code.Should().Be(ErrorCodes.InvalidAuctionConfiguration);
        }

        await using var verify = _fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await db.Auctions.AsNoTracking().FirstAsync(a => a.Id == id);
        saved.ScheduledStartAt.Should().BeCloseTo(original, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_start_time_still_to_come_is_saved()
    {
        var id = await CreateWaitingAuctionAsync(DateTime.UtcNow.AddHours(2));
        var later = DateTime.UtcNow.AddDays(1);

        await using (var scope = _fixture.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();
            await admin.UpdateScheduleAsync(id, new UpdateScheduleRequest(later, "moved a day"));
        }

        await using var verify = _fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await db.Auctions.AsNoTracking().FirstAsync(a => a.Id == id);
        saved.ScheduledStartAt.Should().BeCloseTo(later, TimeSpan.FromSeconds(1));
    }
}
