using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Notifications;

/// <summary>Who a message from the dashboard would reach right now.</summary>
public sealed record NotificationAudienceDto(int Users, int Phones);

/// <param name="AuctionId">Optional: tapping the notification opens this auction.</param>
public sealed record SendAdminMessageRequest(string Title, string Body, Guid? AuctionId = null);

public sealed record AdminMessageSentDto(int Users);

public interface IAdminNotificationService
{
    Task<NotificationAudienceDto> GetAudienceAsync(CancellationToken ct = default);

    Task<AdminMessageSentDto> SendAsync(SendAdminMessageRequest request, CancellationToken ct = default);
}

/// <summary>
/// A message staff write in the dashboard and send to every user, as a push and in the app's
/// notification list. The text goes out exactly as written, in every user's language.
/// </summary>
public sealed class AdminNotificationService : IAdminNotificationService
{
    public const int TitleMax = 100;
    public const int BodyMax = 1000;

    private readonly IApplicationDbContext _db;
    private readonly INotificationBroadcaster _broadcaster;
    private readonly IAuditService _audit;

    public AdminNotificationService(
        IApplicationDbContext db, INotificationBroadcaster broadcaster, IAuditService audit)
    {
        _db = db;
        _broadcaster = broadcaster;
        _audit = audit;
    }

    private IQueryable<Domain.Entities.ApplicationUser> Audience =>
        _db.Users.AsNoTracking().Where(u => u.IsActive && u.NotificationsEnabled);

    public async Task<NotificationAudienceDto> GetAudienceAsync(CancellationToken ct = default)
    {
        var users = await Audience.CountAsync(ct);
        var phones = await _db.UserDeviceTokens.AsNoTracking()
            .CountAsync(t => t.User!.IsActive && t.User.NotificationsEnabled, ct);

        return new NotificationAudienceDto(users, phones);
    }

    public async Task<AdminMessageSentDto> SendAsync(SendAdminMessageRequest request, CancellationToken ct = default)
    {
        var title = (request.Title ?? string.Empty).Trim();
        var body = (request.Body ?? string.Empty).Trim();

        if (title.Length == 0) Fail("Write a title.");
        if (title.Length > TitleMax) Fail($"The title can be at most {TitleMax} characters.");
        if (body.Length == 0) Fail("Write the message.");
        if (body.Length > BodyMax) Fail($"The message can be at most {BodyMax} characters.");

        var data = new Dictionary<string, string> { ["type"] = "ADMIN_MESSAGE" };

        if (request.AuctionId is { } auctionId)
        {
            var status = await _db.Auctions.AsNoTracking()
                .Where(a => a.Id == auctionId).Select(a => (AuctionStatus?)a.Status).FirstOrDefaultAsync(ct);

            if (status is null)
                throw new DomainException(ErrorCodes.AuctionNotFound, "Auction not found.");

            // A draft is invisible to users, so a link to it would open an error.
            if (status == AuctionStatus.Draft) Fail("Publish the auction before linking to it.");

            data["auctionId"] = auctionId.ToString();
        }

        var users = await Audience.CountAsync(ct);

        await _audit.LogAsync(AuditActionType.NotificationBroadcast, "Notification", request.AuctionId,
            null, new { title, body, request.AuctionId, users }, ct: ct);

        await _broadcaster.EnqueueAsync(new NotificationTemplate(
            NotificationType.AdminMessage,
            NotificationKeys.AdminMessageTitle,
            NotificationKeys.AdminMessageBody,
            new object[] { body },
            data,
            new object[] { title }), ct);

        return new AdminMessageSentDto(users);
    }

    private static void Fail(string message) =>
        throw new DomainException(ErrorCodes.ValidationFailed, message);
}
