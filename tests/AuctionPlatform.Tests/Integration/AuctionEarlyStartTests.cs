using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>An admin may start an auction before it is full: with at least one participant and a reason.</summary>
[Collection("database")]
public class AuctionEarlyStartTests
{
    private readonly DatabaseFixture _fixture;

    public AuctionEarlyStartTests(DatabaseFixture fixture) => _fixture = fixture;

    /// <summary>An auction taking participants, with the given number joined of 30 required.</summary>
    private async Task<(Guid AuctionId, List<Guid> Users)> FillingUpAsync(int joined)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 1000m, requiredParticipants: 30);
        auction.Status = AuctionStatus.RegistrationOpen;
        auction.ActualStartedAt = null;
        await db.SaveChangesAsync();

        var users = new List<Guid>();
        for (var i = 0; i < joined; i++)
        {
            var user = await TestData.CreateUserAsync(db, $"Early {i}");
            await TestData.AddParticipantAsync(db, auction.Id, user.Id);
            users.Add(user.Id);
        }
        return (auction.Id, users);
    }

    private async Task<Domain.Entities.Auction> LoadAsync(Guid id)
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .Auctions.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    [Fact]
    public async Task An_auction_can_start_before_it_is_full_with_a_reason()
    {
        var (id, users) = await FillingUpAsync(joined: 3);

        await using (var scope = _fixture.CreateScope())
        {
            var started = await scope.ServiceProvider.GetRequiredService<IAuctionAdminService>()
                .StartNowAsync(id, earlyStartReason: "Weekend promotion");
            started.Status.Should().Be(AuctionStatus.Live);
        }

        var auction = await LoadAsync(id);
        auction.Status.Should().Be(AuctionStatus.Live);
        auction.ActualStartedAt.Should().NotBeNull();
        auction.EndsAt.Should().NotBeNull();

        await using var verify = _fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Those who joined are told, as for any start.
        (await db.Notifications.CountAsync(n => users.Contains(n.UserId) && n.Type == NotificationType.AuctionStarted))
            .Should().Be(3);

        // The reason and how full it was are on record.
        var logged = await db.AuditLogs.AsNoTracking()
            .Where(l => l.Action == AuditActionType.AuctionStarted && l.EntityId == id)
            .Select(l => l.Reason).SingleAsync();
        logged.Should().Contain("3 of 30").And.Contain("Weekend promotion");
    }

    [Fact]
    public async Task An_early_start_needs_a_reason_and_somebody_in_it()
    {
        var (withPeople, _) = await FillingUpAsync(joined: 2);
        var (empty, _) = await FillingUpAsync(joined: 0);

        await using var scope = _fixture.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();

        var noReason = () => admin.StartNowAsync(withPeople);
        var nobody = () => admin.StartNowAsync(empty, earlyStartReason: "Try it");

        (await noReason.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
        (await nobody.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AuctionNotReady);

        (await LoadAsync(withPeople)).Status.Should().Be(AuctionStatus.RegistrationOpen);
        (await LoadAsync(empty)).Status.Should().Be(AuctionStatus.RegistrationOpen);
    }
}
