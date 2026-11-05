using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// Notifications staff send to everyone: a message written in the dashboard, a newly published
/// auction, and the start of an auction for the people in it.
/// </summary>
[Collection("database")]
public class AdminNotificationTests
{
    private readonly DatabaseFixture _fixture;

    public AdminNotificationTests(DatabaseFixture fixture) => _fixture = fixture;

    private static IAdminNotificationService Admin(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IAdminNotificationService>();

    private NotificationTemplate? QueuedFor(string key, Func<NotificationTemplate, bool> match) =>
        _fixture.Broadcasts.Queued.LastOrDefault(t => t.TitleKey == key && match(t));

    [Fact]
    public async Task A_message_goes_out_exactly_as_written_and_is_recorded()
    {
        var tag = Guid.NewGuid().ToString("N");
        var title = $"عرض {tag}";

        await using (var scope = _fixture.CreateScope())
        {
            var sent = await Admin(scope).SendAsync(new SendAdminMessageRequest($"  {title} ", " خصم 20% اليوم فقط "));
            sent.Users.Should().BeGreaterThanOrEqualTo(0);
        }

        var queued = QueuedFor(NotificationKeys.AdminMessageTitle, t => (string)t.TitleArgs![0] == title);
        queued.Should().NotBeNull();
        queued!.Type.Should().Be(NotificationType.AdminMessage);
        queued.BodyArgs!.Single().Should().Be("خصم 20% اليوم فقط");
        queued.Data!["type"].Should().Be("ADMIN_MESSAGE");
        queued.Data.Should().NotContainKey("auctionId");

        await using var verify = _fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var logged = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActionType.NotificationBroadcast)
            .Select(a => a.NewValue).ToListAsync();
        logged.Should().Contain(v => v != null && v.Contains(tag));
    }

    [Theory]
    [InlineData("", "body")]
    [InlineData("   ", "body")]
    [InlineData("title", "")]
    [InlineData("title", "  ")]
    public async Task An_empty_title_or_message_is_refused(string title, string body)
    {
        await using var scope = _fixture.CreateScope();

        var act = () => Admin(scope).SendAsync(new SendAdminMessageRequest(title, body));

        await act.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Text_that_is_too_long_is_refused()
    {
        await using var scope = _fixture.CreateScope();

        var longTitle = () => Admin(scope).SendAsync(new SendAdminMessageRequest(
            new string('x', AdminNotificationService.TitleMax + 1), "body"));
        var longBody = () => Admin(scope).SendAsync(new SendAdminMessageRequest(
            "title", new string('x', AdminNotificationService.BodyMax + 1)));

        await longTitle.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.ValidationFailed);
        await longBody.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task A_message_can_open_a_published_auction_but_not_a_draft_or_a_missing_one()
    {
        Guid live, draft;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            live = (await TestData.CreateLiveAuctionAsync(db)).Id;

            var d = await TestData.CreateLiveAuctionAsync(db);
            d.Status = AuctionStatus.Draft;
            d.ActualStartedAt = null;
            await db.SaveChangesAsync();
            draft = d.Id;
        }

        await using (var scope = _fixture.CreateScope())
        {
            await Admin(scope).SendAsync(new SendAdminMessageRequest("مزاد مباشر", "ادخل الآن", live));

            var toDraft = () => Admin(scope).SendAsync(new SendAdminMessageRequest("t", "b", draft));
            await toDraft.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.ValidationFailed);

            var toNothing = () => Admin(scope).SendAsync(new SendAdminMessageRequest("t", "b", Guid.NewGuid()));
            await toNothing.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.AuctionNotFound);
        }

        QueuedFor(NotificationKeys.AdminMessageTitle, t => t.Data!.GetValueOrDefault("auctionId") == live.ToString())
            .Should().NotBeNull();
        _fixture.Broadcasts.Queued
            .Should().NotContain(t => t.Data != null && t.Data.GetValueOrDefault("auctionId") == draft.ToString());
    }

    [Fact]
    public async Task Everyone_with_notifications_on_gets_it_and_nobody_else()
    {
        Guid on, off, closed;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            on = (await TestData.CreateUserAsync(db, "On")).Id;

            var o = await TestData.CreateUserAsync(db, "Off");
            o.NotificationsEnabled = false;
            var c = await TestData.CreateUserAsync(db, "Closed");
            c.IsActive = false;
            await db.SaveChangesAsync();
            off = o.Id;
            closed = c.Id;
        }

        var title = $"رسالة {Guid.NewGuid():N}";
        int users;
        await using (var scope = _fixture.CreateScope())
        {
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
            users = await notifications.NotifyEveryoneAsync(new NotificationTemplate(
                NotificationType.AdminMessage,
                NotificationKeys.AdminMessageTitle,
                NotificationKeys.AdminMessageBody,
                new object[] { "النص {كما هو}" },
                new Dictionary<string, string> { ["type"] = "ADMIN_MESSAGE" },
                new object[] { title }));
        }

        await using var verify = _fixture.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var got = await vdb.Notifications.AsNoTracking().Where(n => n.Title == title).ToListAsync();

        got.Should().ContainSingle(n => n.UserId == on)
            .Which.Body.Should().Be("النص {كما هو}", "braces in the text are not a template");
        got.Should().NotContain(n => n.UserId == off || n.UserId == closed);
        got.Should().HaveCount(users, "each account gets it once");

        var expected = await vdb.Users.CountAsync(u => u.IsActive && u.NotificationsEnabled);
        users.Should().BeGreaterThanOrEqualTo(1).And.BeLessThanOrEqualTo(expected);
    }

    [Fact]
    public async Task Publishing_an_auction_tells_everyone_about_it()
    {
        Guid id;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db);
            auction.Status = AuctionStatus.Draft;
            auction.ActualStartedAt = null;
            auction.Title = "آيفون للنشر";
            await db.SaveChangesAsync();
            id = auction.Id;
        }

        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuctionAdminService>().PublishAsync(id);
        }

        var queued = QueuedFor(NotificationKeys.NewAuctionTitle, t => t.Data!["auctionId"] == id.ToString());
        queued.Should().NotBeNull();
        queued!.Type.Should().Be(NotificationType.NewAuction);
        queued.BodyArgs!.Single().Should().Be("آيفون للنشر");
        queued.Data!["type"].Should().Be("NEW_AUCTION");
    }

    [Fact]
    public async Task Starting_an_auction_tells_its_participants_which_one()
    {
        Guid id, userId;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 1000m);
            auction.Status = AuctionStatus.Ready;
            auction.ActualStartedAt = null;
            auction.Title = $"سامسونج {Guid.NewGuid():N}"[..20];
            await db.SaveChangesAsync();
            id = auction.Id;

            var user = await TestData.CreateUserAsync(db, "Participant");
            userId = user.Id;
            await TestData.AddParticipantAsync(db, id, userId);
        }

        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuctionAdminService>().StartNowAsync(id);
        }

        await using var verify = _fixture.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var title = await vdb.Auctions.Where(a => a.Id == id).Select(a => a.Title).FirstAsync();
        var note = await vdb.Notifications.AsNoTracking()
            .SingleAsync(n => n.UserId == userId && n.Type == NotificationType.AuctionStarted);

        note.Body.Should().Contain(title);
    }
}
