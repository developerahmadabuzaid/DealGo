using AuctionPlatform.Application.Site;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The advertisements on the app's front page: what an administrator saves, what the app is then
/// given, and what it is not given.
/// </summary>
[Collection("database")]
public class AppAdTests
{
    private readonly DatabaseFixture _fixture;

    public AppAdTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_advertisement_pointing_at_an_auction_carries_the_address_the_app_opens()
    {
        Guid auctionId;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            auctionId = (await TestData.CreateLiveAuctionAsync(db)).Id;
        }

        await using var work = _fixture.CreateScope();
        var ads = work.ServiceProvider.GetRequiredService<IAppAdService>();

        var saved = await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeSlideshow",
            ImageUrl: "/uploads/campaign.png",
            Title: "عرض الأسبوع",
            Note: "وفّر حتى 60%",
            TargetKind: "Auction",
            TargetId: auctionId));

        saved.Route.Should().Be($"/auctions/{auctionId}");
        saved.Placement.Should().Be("HomeSlideshow");

        var live = await ads.GetLiveAsync();
        live.Should().Contain(a => a.Id == saved.Id);

        // Every change is recorded, the same as any other edit an administrator makes.
        var db2 = work.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db2.AuditLogs.AsNoTracking()
            .CountAsync(l => l.Action == AuditActionType.AppAdChanged && l.EntityId == saved.Id))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_draft_and_a_campaign_that_has_not_started_stay_out_of_the_app()
    {
        await using var scope = _fixture.CreateScope();
        var ads = scope.ServiceProvider.GetRequiredService<IAppAdService>();

        var draft = await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip", ImageUrl: "/uploads/draft.png", IsPublished: false));

        var later = await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip",
            ImageUrl: "/uploads/later.png",
            StartsAt: DateTime.UtcNow.AddDays(3)));

        var finished = await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip",
            ImageUrl: "/uploads/over.png",
            EndsAt: DateTime.UtcNow.AddDays(-1)));

        var running = await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip",
            ImageUrl: "/uploads/now.png",
            StartsAt: DateTime.UtcNow.AddHours(-1),
            EndsAt: DateTime.UtcNow.AddDays(1)));

        var live = await ads.GetLiveAsync();
        var ids = live.Select(a => a.Id).ToList();

        ids.Should().Contain(running.Id);
        ids.Should().NotContain(draft.Id).And.NotContain(later.Id).And.NotContain(finished.Id);

        // The console still sees all four, or nobody could ever publish the draft.
        (await ads.GetAllAsync()).Select(a => a.Id)
            .Should().Contain(new[] { draft.Id, later.Id, finished.Id, running.Id });
    }

    /// <summary>
    /// A WhatsApp advertisement opens a conversation with the advertiser. The number is accepted
    /// in whatever shape it was typed and stored in one form, so the address built from it is the
    /// same either way.
    /// </summary>
    [Theory]
    [InlineData("0501234567")]
    [InlineData("501234567")]
    [InlineData("+966 50 123 4567")]
    [InlineData("00966501234567")]
    public async Task A_whatsapp_advertisement_opens_a_chat_with_that_number(string typed)
    {
        await using var scope = _fixture.CreateScope();
        var ads = scope.ServiceProvider.GetRequiredService<IAppAdService>();

        var saved = await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip",
            ImageUrl: "/uploads/a.png",
            TargetKind: "WhatsApp",
            TargetUrl: typed));

        saved.TargetKind.Should().Be("WhatsApp");

        // Stored once, canonically, whatever was typed.
        saved.TargetUrl.Should().Be("+966501234567");

        // wa.me takes digits alone; a leading + there opens nothing.
        saved.Route.Should().Be("https://wa.me/966501234567");
    }

    [Fact]
    public async Task A_whatsapp_advertisement_needs_a_number_that_can_be_reached()
    {
        await using var scope = _fixture.CreateScope();
        var ads = scope.ServiceProvider.GetRequiredService<IAppAdService>();

        var missing = async () => await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip", ImageUrl: "/uploads/a.png", TargetKind: "WhatsApp"));

        var notAPhone = async () => await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip", ImageUrl: "/uploads/a.png",
            TargetKind: "WhatsApp", TargetUrl: "call me maybe"));

        // A landline is a real Saudi number and still cannot hold a WhatsApp conversation.
        var landline = async () => await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip", ImageUrl: "/uploads/a.png",
            TargetKind: "WhatsApp", TargetUrl: "0112345678"));

        foreach (var attempt in new[] { missing, notAPhone, landline })
        {
            await attempt.Should().ThrowAsync<DomainException>();
        }
    }

    /// <summary>Changing the target away from WhatsApp must not leave the number behind.</summary>
    [Fact]
    public async Task Changing_the_target_clears_the_number()
    {
        await using var scope = _fixture.CreateScope();
        var ads = scope.ServiceProvider.GetRequiredService<IAppAdService>();

        var saved = await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip",
            ImageUrl: "/uploads/a.png",
            TargetKind: "WhatsApp",
            TargetUrl: "0501234567"));

        var changed = await ads.SaveAsync(saved.Id, new AppAdRequest(
            Placement: "HomeStrip",
            ImageUrl: "/uploads/a.png",
            TargetKind: "None"));

        changed.TargetUrl.Should().BeNull();
        changed.Route.Should().BeNull();
    }

    [Fact]
    public async Task An_advertisement_without_a_picture_or_with_a_target_it_cannot_reach_is_refused()
    {
        await using var scope = _fixture.CreateScope();
        var ads = scope.ServiceProvider.GetRequiredService<IAppAdService>();

        var noPicture = async () => await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip", ImageUrl: "  "));

        var noTarget = async () => await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip", ImageUrl: "/uploads/a.png", TargetKind: "Product"));

        var noAddress = async () => await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip", ImageUrl: "/uploads/a.png", TargetKind: "Url"));

        var nowhere = async () => await ads.SaveAsync(null, new AppAdRequest(
            Placement: "Nowhere", ImageUrl: "/uploads/a.png"));

        var backwards = async () => await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeStrip",
            ImageUrl: "/uploads/a.png",
            StartsAt: DateTime.UtcNow.AddDays(2),
            EndsAt: DateTime.UtcNow.AddDays(1)));

        foreach (var attempt in new[] { noPicture, noTarget, noAddress, nowhere, backwards })
        {
            (await attempt.Should().ThrowAsync<DomainException>())
                .Which.Code.Should().Be(ErrorCodes.ValidationFailed);
        }
    }

    [Fact]
    public async Task Deleting_one_takes_it_out_of_the_app_and_leaves_the_others_alone()
    {
        await using var scope = _fixture.CreateScope();
        var ads = scope.ServiceProvider.GetRequiredService<IAppAdService>();

        var first = await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeUnderCategories", ImageUrl: "/uploads/1.png", DisplayOrder: 1));

        var second = await ads.SaveAsync(null, new AppAdRequest(
            Placement: "HomeUnderCategories", ImageUrl: "/uploads/2.png", DisplayOrder: 2));

        await ads.DeleteAsync(first.Id);

        var live = await ads.GetLiveAsync();
        live.Select(a => a.Id).Should().Contain(second.Id).And.NotContain(first.Id);

        // Deleting something that is already gone is not an error; the row is simply not there.
        await ads.DeleteAsync(first.Id);
    }
}
