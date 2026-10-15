using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// An administrator adding a user to an auction at a fee of their choosing. The fee comes from the
/// user's Main wallet like any other entry fee, so a user who cannot cover it is refused outright.
/// </summary>
[Collection("database")]
public class AdminAddParticipantTests
{
    private readonly DatabaseFixture _fixture;

    public AdminAddParticipantTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task<(Guid AuctionId, Guid RichUser, Guid PoorUser)> SetUpAsync()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var auction = await TestData.CreateLiveAuctionAsync(db, requiredParticipants: 10);
        auction.Status = AuctionStatus.RegistrationOpen;
        auction.ActualStartedAt = null;

        var rich = await TestData.CreateUserAsync(db, "Has enough", mainBalance: 100m, shoppingBalance: 500m);
        var poor = await TestData.CreateUserAsync(db, "Short", mainBalance: 30m, shoppingBalance: 500m);
        await db.SaveChangesAsync();

        return (auction.Id, rich.Id, poor.Id);
    }

    [Fact]
    public async Task The_administrators_fee_is_charged_to_the_main_wallet()
    {
        var (auctionId, rich, _) = await SetUpAsync();

        await using (var scope = _fixture.CreateScope())
        {
            var participation = scope.ServiceProvider.GetRequiredService<IParticipationService>();
            var added = await participation.AddByAdminAsync(auctionId, new AddParticipantRequest(rich, 75m));
            added.EntryFeePaid.Should().Be(75m, "the administrator's fee, not the auction's 20");
        }

        await using var verify = _fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var main = await db.Wallets.AsNoTracking()
            .FirstAsync(w => w.UserId == rich && w.WalletType == WalletType.Main);
        var shopping = await db.Wallets.AsNoTracking()
            .FirstAsync(w => w.UserId == rich && w.WalletType == WalletType.Shopping);
        main.Balance.Should().Be(25m);
        shopping.Balance.Should().Be(500m, "shopping credit never pays an entry fee");

        var auction = await db.Auctions.AsNoTracking().FirstAsync(a => a.Id == auctionId);
        auction.ParticipantCount.Should().Be(1);
        auction.Status.Should().Be(AuctionStatus.WaitingForParticipants);

        (await db.AuditLogs.AsNoTracking()
                .AnyAsync(l => l.Action == AuditActionType.ParticipantAddedByAdmin))
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_user_who_cannot_cover_the_fee_is_refused_and_nothing_changes()
    {
        var (auctionId, _, poor) = await SetUpAsync();

        await using (var scope = _fixture.CreateScope())
        {
            var participation = scope.ServiceProvider.GetRequiredService<IParticipationService>();
            var act = () => participation.AddByAdminAsync(auctionId, new AddParticipantRequest(poor, 50m));

            (await act.Should().ThrowAsync<DomainException>())
                .Which.Code.Should().Be(ErrorCodes.InsufficientBalance);
        }

        await using var verify = _fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        (await db.AuctionParticipants.AsNoTracking().AnyAsync(p => p.AuctionId == auctionId))
            .Should().BeFalse();
        (await db.Auctions.AsNoTracking().FirstAsync(a => a.Id == auctionId)).ParticipantCount
            .Should().Be(0);
        (await db.Wallets.AsNoTracking().FirstAsync(w => w.UserId == poor && w.WalletType == WalletType.Main))
            .Balance.Should().Be(30m);
    }

    [Fact]
    public async Task The_same_user_cannot_be_added_twice()
    {
        var (auctionId, rich, _) = await SetUpAsync();

        await using var scope = _fixture.CreateScope();
        var participation = scope.ServiceProvider.GetRequiredService<IParticipationService>();
        await participation.AddByAdminAsync(auctionId, new AddParticipantRequest(rich, 10m));

        var again = () => participation.AddByAdminAsync(auctionId, new AddParticipantRequest(rich, 10m));
        (await again.Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(ErrorCodes.AlreadyJoined);
    }
}
