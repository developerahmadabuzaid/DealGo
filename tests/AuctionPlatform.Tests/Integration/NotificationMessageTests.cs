using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The wording staff give the automatic messages, switching them off, and messages scheduled to go
/// out later.
/// </summary>
[Collection("database")]
public class NotificationMessageTests
{
    private readonly DatabaseFixture _fixture;

    public NotificationMessageTests(DatabaseFixture fixture) => _fixture = fixture;

    private static IAutomaticMessageService Automatic(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IAutomaticMessageService>();

    private static IAdminNotificationService Admin(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IAdminNotificationService>();

    /// <summary>An auction about to start, with one participant in the given language.</summary>
    private async Task<(Guid AuctionId, string Title, Guid UserId)> ReadyAuctionAsync(string language = "ar")
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 1000m);
        auction.Status = AuctionStatus.Ready;
        auction.ActualStartedAt = null;
        auction.Title = $"ساعة {Guid.NewGuid():N}"[..16];
        await db.SaveChangesAsync();

        var user = await TestData.CreateUserAsync(db, "Participant");
        user.Language = language;
        await db.SaveChangesAsync();
        await TestData.AddParticipantAsync(db, auction.Id, user.Id);
        return (auction.Id, auction.Title, user.Id);
    }

    private async Task StartAsync(Guid auctionId)
    {
        await using var scope = _fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAuctionAdminService>().StartNowAsync(auctionId);
    }

    private async Task<Notification?> StartedNoteAsync(Guid userId)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Notifications.AsNoTracking()
            .SingleOrDefaultAsync(n => n.UserId == userId && n.Type == NotificationType.AuctionStarted);
    }

    private async Task ResetAsync(string code)
    {
        await using var scope = _fixture.CreateScope();
        await Automatic(scope).ResetAsync(code);
    }

    [Fact]
    public async Task Every_automatic_message_is_listed_with_its_built_in_wording()
    {
        await using var scope = _fixture.CreateScope();
        var list = await Automatic(scope).ListAsync();

        list.Select(m => m.Code).Should().Contain(["new_auction", "auction_started", "you_won", "order_shipped"]);
        var started = list.Single(m => m.Code == "auction_started");
        started.Arabic.DefaultBody.Should().Contain("{auction}").And.NotContain("{0}");
        started.English.DefaultBody.Should().Contain("{auction}");
        started.Placeholders.Select(p => p.Token).Should().Equal("auction");
    }

    [Fact]
    public async Task Staff_wording_goes_out_with_its_placeholders_filled_in_each_language()
    {
        try
        {
            await using (var scope = _fixture.CreateScope())
            {
                await Automatic(scope).UpdateAsync("auction_started", new UpdateAutomaticMessageRequest(
                    true, "انطلق {auction}!", "المزاد «{auction}» بدأ، ادخل الآن.", null, "Go! {auction} is live."));
            }

            var arabic = await ReadyAuctionAsync("ar");
            await StartAsync(arabic.AuctionId);
            var ar = await StartedNoteAsync(arabic.UserId);
            ar!.Title.Should().Be($"انطلق {arabic.Title}!");
            ar.Body.Should().Be($"المزاد «{arabic.Title}» بدأ، ادخل الآن.");

            var english = await ReadyAuctionAsync("en");
            await StartAsync(english.AuctionId);
            var en = await StartedNoteAsync(english.UserId);
            en!.Title.Should().Be("The auction has started");   // left empty: the built-in title
            en.Body.Should().Be($"Go! {english.Title} is live.");
        }
        finally
        {
            await ResetAsync("auction_started");
        }
    }

    [Fact]
    public async Task A_message_switched_off_is_not_sent()
    {
        try
        {
            await using (var scope = _fixture.CreateScope())
            {
                await Automatic(scope).UpdateAsync("auction_started",
                    new UpdateAutomaticMessageRequest(false, null, null, null, null));
            }

            var a = await ReadyAuctionAsync();
            await StartAsync(a.AuctionId);
            (await StartedNoteAsync(a.UserId)).Should().BeNull();
        }
        finally
        {
            await ResetAsync("auction_started");
        }

        // Switched back on by the reset.
        var b = await ReadyAuctionAsync();
        await StartAsync(b.AuctionId);
        (await StartedNoteAsync(b.UserId))!.Body.Should().Contain(b.Title);
    }

    [Fact]
    public async Task A_placeholder_the_message_cannot_fill_is_refused()
    {
        await using var scope = _fixture.CreateScope();
        var act = () => Automatic(scope).UpdateAsync("you_lost",
            new UpdateAutomaticMessageRequest(true, null, "خسرت مزاد {auction}", null, null));

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task An_unknown_message_is_not_found()
    {
        await using var scope = _fixture.CreateScope();
        var act = () => Automatic(scope).UpdateAsync("nope", new UpdateAutomaticMessageRequest(true, null, null, null, null));
        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.NotFound);
    }

    // ---------------------------------------------------------------- scheduled

    [Fact]
    public async Task A_time_in_the_past_or_too_far_ahead_is_refused()
    {
        await using var scope = _fixture.CreateScope();
        var past = () => Admin(scope).ScheduleAsync(new ScheduleAdminMessageRequest("t", "b", DateTime.UtcNow.AddMinutes(-5)));
        var far = () => Admin(scope).ScheduleAsync(new ScheduleAdminMessageRequest("t", "b", DateTime.UtcNow.AddDays(120)));

        (await past.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
        (await far.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Participants_as_the_audience_need_an_auction()
    {
        await using var scope = _fixture.CreateScope();
        var act = () => Admin(scope).ScheduleAsync(new ScheduleAdminMessageRequest(
            "t", "b", DateTime.UtcNow.AddHours(1), null, ScheduledNotificationAudience.AuctionParticipants));

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task A_scheduled_message_can_be_changed_and_cancelled_until_it_goes_out()
    {
        await using var scope = _fixture.CreateScope();
        var admin = Admin(scope);
        var tag = Guid.NewGuid().ToString("N")[..8];

        var s = await admin.ScheduleAsync(new ScheduleAdminMessageRequest($"عرض {tag}", "نص", DateTime.UtcNow.AddHours(2)));
        s.Status.Should().Be(ScheduledNotificationStatus.Scheduled);

        var later = DateTime.UtcNow.AddDays(1);
        var changed = await admin.UpdateScheduledAsync(s.Id, new ScheduleAdminMessageRequest($"عرض {tag} ٢", "نص جديد", later));
        changed.Title.Should().Be($"عرض {tag} ٢");
        changed.SendAt.Should().BeCloseTo(later, TimeSpan.FromSeconds(1));

        (await admin.CancelScheduledAsync(s.Id)).Status.Should().Be(ScheduledNotificationStatus.Cancelled);

        var again = () => admin.UpdateScheduledAsync(s.Id, new ScheduleAdminMessageRequest("x", "y", later));
        (await again.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.Conflict);

        var listed = await admin.GetScheduledAsync(new ScheduledNotificationQuery { PageSize = 100 });
        listed.Items.Should().Contain(i => i.Id == s.Id && i.Status == ScheduledNotificationStatus.Cancelled);
    }

    [Fact]
    public async Task A_due_message_goes_to_the_auctions_participants_once()
    {
        var a = await ReadyAuctionAsync();
        Guid outsider;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            outsider = (await TestData.CreateUserAsync(db, "Outsider")).Id;
        }

        var tag = Guid.NewGuid().ToString("N")[..8];
        Guid id;
        await using (var scope = _fixture.CreateScope())
        {
            id = (await Admin(scope).ScheduleAsync(new ScheduleAdminMessageRequest(
                $"تذكير {tag}", "يبدأ المزاد قريباً", DateTime.UtcNow.AddMinutes(5),
                a.AuctionId, ScheduledNotificationAudience.AuctionParticipants))).Id;

            // Not due yet: nothing goes out.
            await Admin(scope).SendDueAsync();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.ScheduledNotifications.AsNoTracking().SingleAsync(s => s.Id == id)).Status
                .Should().Be(ScheduledNotificationStatus.Scheduled);

            // Its time comes.
            await db.ScheduledNotifications.Where(s => s.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.SendAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        await using (var scope = _fixture.CreateScope())
        {
            await Admin(scope).SendDueAsync();
            await Admin(scope).SendDueAsync();   // a second pass sends nothing more
        }

        await using var verify = _fixture.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await vdb.ScheduledNotifications.AsNoTracking().SingleAsync(s => s.Id == id);
        row.Status.Should().Be(ScheduledNotificationStatus.Sent);
        row.Recipients.Should().Be(1);
        row.SentAt.Should().NotBeNull();

        var notes = await vdb.Notifications.AsNoTracking().Where(n => n.Title == $"تذكير {tag}").ToListAsync();
        notes.Should().ContainSingle().Which.UserId.Should().Be(a.UserId);
        notes.Should().NotContain(n => n.UserId == outsider);
        notes[0].Data.Should().Contain(a.AuctionId.ToString());
    }
}
