using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Site;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The auction policies written in the dashboard: while they are published, every join has to
/// accept their current version, and the participant row keeps which version that was.
/// </summary>
[Collection("database")]
public class AuctionPolicyTests
{
    private readonly DatabaseFixture _fixture;

    public AuctionPolicyTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task<AuctionPolicyDto> SavePolicyAsync(string? body, string? title = null)
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAuctionPolicyService>()
            .UpdateAsync(new UpdateAuctionPolicyRequest(title, body));
    }

    private async Task<Guid> PublishedDemoAsync()
    {
        await using var scope = _fixture.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();
        var created = await admin.CreateAsync(new CreateAuctionRequest(
            Title: "مزاد السياسات",
            Description: null,
            StartingPercentage: 10m,
            TargetPercentage: 50m,
            FinalPhasePercentage: 40m,
            BidIncrement: 100m,
            EntryFee: 0m,
            Currency: null,
            RequiredParticipants: 10,
            StartDelayAfterParticipantsSeconds: 0,
            DurationSeconds: 600,
            ExpirationPolicy: ExpirationPolicy.EndAndDistribute,
            Prizes: Array.Empty<AuctionPrizeRequest>(),
            IsDemo: true,
            DemoValue: 1000m));
        await admin.PublishAsync(created.Id);
        return created.Id;
    }

    private async Task<Guid> UserAsync()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await TestData.CreateUserAsync(db, "Policy reader", mainBalance: 0m)).Id;
    }

    private async Task<MyParticipationDto> JoinAsync(Guid auctionId, Guid userId, int? acceptedVersion)
    {
        await using var scope = _fixture.CreateScope(userId);
        return await scope.ServiceProvider.GetRequiredService<IParticipationService>()
            .JoinAsync(auctionId, userId, $"join-{Guid.NewGuid():N}", acceptedVersion);
    }

    [Fact]
    public async Task While_policies_are_published_a_join_must_accept_their_current_version()
    {
        try
        {
            var first = await SavePolicyAsync("الفائز يدفع خلال 24 ساعة.");
            var current = await SavePolicyAsync("الفائز يدفع خلال 24 ساعة وإلا يفقد الجهاز.");
            current.Required.Should().BeTrue();
            current.Version.Should().Be(first.Version + 1);

            var auction = await PublishedDemoAsync();
            var user = await UserAsync();

            var none = () => JoinAsync(auction, user, null);
            (await none.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.PoliciesNotAccepted);

            var stale = () => JoinAsync(auction, user, first.Version);
            (await stale.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.PoliciesChanged);

            await JoinAsync(auction, user, current.Version);

            await using var scope = _fixture.CreateScope();
            var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .AuctionParticipants.AsNoTracking().SingleAsync(p => p.AuctionId == auction && p.UserId == user);
            row.PolicyVersionAccepted.Should().Be(current.Version);
            row.PolicyAcceptedAt.Should().NotBeNull();
        }
        finally
        {
            await SavePolicyAsync(null);
        }
    }

    [Fact]
    public async Task Saving_the_same_words_keeps_the_version_and_an_empty_text_asks_for_nothing()
    {
        try
        {
            var saved = await SavePolicyAsync("نص السياسات.", "سياسات");
            var again = await SavePolicyAsync("نص السياسات.", "سياسات");
            again.Version.Should().Be(saved.Version, "nobody is asked to accept the same words twice");

            var cleared = await SavePolicyAsync("   ");
            cleared.Required.Should().BeFalse();

            var auction = await PublishedDemoAsync();
            var user = await UserAsync();
            await JoinAsync(auction, user, null);

            await using var scope = _fixture.CreateScope();
            var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .AuctionParticipants.AsNoTracking().SingleAsync(p => p.AuctionId == auction && p.UserId == user);
            row.PolicyVersionAccepted.Should().BeNull();
        }
        finally
        {
            await SavePolicyAsync(null);
        }
    }
}
