using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuctionPlatform.Application.Notifications;

/// <summary>Who a message from the dashboard would reach right now.</summary>
public sealed record NotificationAudienceDto(int Users, int Phones);

/// <param name="AuctionId">Optional: tapping the notification opens this auction.</param>
/// <param name="Audience">Everyone, or only the people in <paramref name="AuctionId"/>.</param>
public sealed record SendAdminMessageRequest(
    string Title,
    string Body,
    Guid? AuctionId = null,
    ScheduledNotificationAudience Audience = ScheduledNotificationAudience.Everyone);

public sealed record AdminMessageSentDto(int Users);

/// <summary>A message to go out later. <c>SendAt</c> is UTC.</summary>
public sealed record ScheduleAdminMessageRequest(
    string Title,
    string Body,
    DateTime SendAt,
    Guid? AuctionId = null,
    ScheduledNotificationAudience Audience = ScheduledNotificationAudience.Everyone);

public sealed record ScheduledNotificationDto(
    Guid Id,
    string Title,
    string Body,
    Guid? AuctionId,
    string? AuctionTitle,
    ScheduledNotificationAudience Audience,
    DateTime SendAt,
    ScheduledNotificationStatus Status,
    DateTime? SentAt,
    int? Recipients,
    string? Error,
    DateTime CreatedAt);

public sealed class ScheduledNotificationQuery : PagedQuery
{
    public ScheduledNotificationStatus? Status { get; set; }
}

public interface IAdminNotificationService
{
    Task<NotificationAudienceDto> GetAudienceAsync(CancellationToken ct = default);

    Task<AdminMessageSentDto> SendAsync(SendAdminMessageRequest request, CancellationToken ct = default);

    Task<ScheduledNotificationDto> ScheduleAsync(ScheduleAdminMessageRequest request, CancellationToken ct = default);

    /// <summary>Changes a message that has not gone out yet.</summary>
    Task<ScheduledNotificationDto> UpdateScheduledAsync(Guid id, ScheduleAdminMessageRequest request, CancellationToken ct = default);

    Task<ScheduledNotificationDto> CancelScheduledAsync(Guid id, CancellationToken ct = default);

    Task<PagedResult<ScheduledNotificationDto>> GetScheduledAsync(ScheduledNotificationQuery query, CancellationToken ct = default);

    /// <summary>Sends every scheduled message whose time has come. Called by the background worker.</summary>
    Task<int> SendDueAsync(CancellationToken ct = default);
}

/// <summary>
/// Messages staff write in the dashboard: sent at once, or at a time they choose, to every user or
/// to the people in one auction, as a push and in the app's notification list. The text goes out
/// exactly as written, in every user's language.
/// </summary>
public sealed class AdminNotificationService : IAdminNotificationService
{
    public const int TitleMax = 100;
    public const int BodyMax = 1000;

    /// <summary>How far ahead a message may be scheduled.</summary>
    public static readonly TimeSpan MaxScheduleAhead = TimeSpan.FromDays(90);

    /// <summary>A message still "sending" after this was cut off by a restart; it is not sent twice.</summary>
    public static readonly TimeSpan SendingTimeout = TimeSpan.FromMinutes(30);

    private readonly IApplicationDbContext _db;
    private readonly INotificationBroadcaster _broadcaster;
    private readonly INotificationService _notifications;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _user;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<AdminNotificationService> _logger;

    public AdminNotificationService(
        IApplicationDbContext db,
        INotificationBroadcaster broadcaster,
        INotificationService notifications,
        IAuditService audit,
        ICurrentUserService user,
        IDateTimeProvider clock,
        ILogger<AdminNotificationService>? logger = null)
    {
        _db = db;
        _broadcaster = broadcaster;
        _notifications = notifications;
        _audit = audit;
        _user = user;
        _clock = clock;
        _logger = logger ?? NullLogger<AdminNotificationService>.Instance;
    }

    private IQueryable<ApplicationUser> Audience =>
        _db.Users.AsNoTracking().Where(u => u.IsActive && u.NotificationsEnabled);

    public async Task<NotificationAudienceDto> GetAudienceAsync(CancellationToken ct = default)
    {
        var users = await Audience.CountAsync(ct);
        var phones = await _db.UserDeviceTokens.AsNoTracking()
            .CountAsync(t => t.User!.IsActive && t.User.NotificationsEnabled, ct);

        return new NotificationAudienceDto(users, phones);
    }

    // ---------------------------------------------------------------- now

    public async Task<AdminMessageSentDto> SendAsync(SendAdminMessageRequest request, CancellationToken ct = default)
    {
        var message = await ValidateAsync(request.Title, request.Body, request.AuctionId, request.Audience, ct);

        var template = Template(message.Title, message.Body, request.AuctionId);
        int users;

        if (request.Audience == ScheduledNotificationAudience.AuctionParticipants)
        {
            var ids = await ParticipantIdsAsync(request.AuctionId!.Value, ct);
            users = ids.Count;
            await Log(AuditActionType.NotificationBroadcast, null, message, request.AuctionId, request.Audience, users, null, ct);
            await _notifications.NotifyManyAsync(ids, template, ct);
        }
        else
        {
            users = await Audience.CountAsync(ct);
            await Log(AuditActionType.NotificationBroadcast, null, message, request.AuctionId, request.Audience, users, null, ct);
            await _broadcaster.EnqueueAsync(template, ct);
        }

        return new AdminMessageSentDto(users);
    }

    // ---------------------------------------------------------------- later

    public async Task<ScheduledNotificationDto> ScheduleAsync(
        ScheduleAdminMessageRequest request, CancellationToken ct = default)
    {
        var message = await ValidateAsync(request.Title, request.Body, request.AuctionId, request.Audience, ct);
        var sendAt = ValidateTime(request.SendAt);
        var now = _clock.UtcNow;

        var row = new ScheduledNotification
        {
            Title = message.Title,
            Body = message.Body,
            AuctionId = request.AuctionId,
            Audience = request.Audience,
            SendAt = sendAt,
            Status = ScheduledNotificationStatus.Scheduled,
            CreatedByUserId = _user.UserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.ScheduledNotifications.Add(row);
        await _db.SaveChangesAsync(ct);

        await Log(AuditActionType.NotificationScheduled, row.Id, message, request.AuctionId, request.Audience, null, sendAt, ct);
        return await GetAsync(row.Id, ct);
    }

    public async Task<ScheduledNotificationDto> UpdateScheduledAsync(
        Guid id, ScheduleAdminMessageRequest request, CancellationToken ct = default)
    {
        var row = await FindWaitingAsync(id, ct);
        var message = await ValidateAsync(request.Title, request.Body, request.AuctionId, request.Audience, ct);
        var sendAt = ValidateTime(request.SendAt);

        row.Title = message.Title;
        row.Body = message.Body;
        row.AuctionId = request.AuctionId;
        row.Audience = request.Audience;
        row.SendAt = sendAt;
        row.UpdatedAt = _clock.UtcNow;
        await SaveWaitingAsync(ct);

        await Log(AuditActionType.NotificationScheduled, row.Id, message, request.AuctionId, request.Audience, null, sendAt, ct);
        return await GetAsync(row.Id, ct);
    }

    public async Task<ScheduledNotificationDto> CancelScheduledAsync(Guid id, CancellationToken ct = default)
    {
        var row = await FindWaitingAsync(id, ct);
        row.Status = ScheduledNotificationStatus.Cancelled;
        row.UpdatedAt = _clock.UtcNow;
        await SaveWaitingAsync(ct);

        await _audit.LogAsync(AuditActionType.NotificationScheduleCancelled, "ScheduledNotification", row.Id,
            null, new { row.Title, row.SendAt }, ct: ct);
        return await GetAsync(row.Id, ct);
    }

    public async Task<PagedResult<ScheduledNotificationDto>> GetScheduledAsync(
        ScheduledNotificationQuery query, CancellationToken ct = default)
    {
        var q = _db.ScheduledNotifications.AsNoTracking();
        if (query.Status is { } status) q = q.Where(s => s.Status == status);

        var total = await q.LongCountAsync(ct);

        // Waiting ones first, soonest first; then what already went out, newest first.
        var items = await Rows(q
                .OrderBy(s => s.Status != ScheduledNotificationStatus.Scheduled)
                .ThenBy(s => s.Status == ScheduledNotificationStatus.Scheduled ? s.SendAt : DateTime.MaxValue)
                .ThenByDescending(s => s.SendAt)
                .Skip(query.Skip)
                .Take(query.PageSize))
            .ToListAsync(ct);

        return PagedResult<ScheduledNotificationDto>.Create(items, query.Page, query.PageSize, total);
    }

    public async Task<int> SendDueAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        // A message left half-sent by a restart is not sent again: some people already have it.
        var stale = now - SendingTimeout;
        await _db.ScheduledNotifications
            .Where(s => s.Status == ScheduledNotificationStatus.Sending && s.UpdatedAt < stale)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, ScheduledNotificationStatus.Failed)
                .SetProperty(s => s.Error, "The server restarted while this was being sent; some users may not have received it.")
                .SetProperty(s => s.UpdatedAt, now), ct);

        var due = await _db.ScheduledNotifications.AsNoTracking()
            .Where(s => s.Status == ScheduledNotificationStatus.Scheduled && s.SendAt <= now)
            .OrderBy(s => s.SendAt)
            .Select(s => s.Id)
            .Take(20)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var id in due)
        {
            // Claimed in one statement, so two servers never both send it, and an edit or a cancel
            // made in the meantime wins.
            var claimed = await _db.ScheduledNotifications
                .Where(s => s.Id == id && s.Status == ScheduledNotificationStatus.Scheduled)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Status, ScheduledNotificationStatus.Sending)
                    .SetProperty(s => s.UpdatedAt, _clock.UtcNow), ct);
            if (claimed == 0) continue;

            var row = await _db.ScheduledNotifications.AsNoTracking().FirstAsync(s => s.Id == id, ct);
            try
            {
                var template = Template(row.Title, row.Body, row.AuctionId);
                int recipients;
                if (row.Audience == ScheduledNotificationAudience.AuctionParticipants && row.AuctionId is { } auctionId)
                {
                    var ids = await ParticipantIdsAsync(auctionId, ct);
                    recipients = ids.Count;
                    await _notifications.NotifyManyAsync(ids, template, ct);
                }
                else
                {
                    recipients = await _notifications.NotifyEveryoneAsync(template, ct);
                }

                await Finish(id, ScheduledNotificationStatus.Sent, recipients, null, ct);
                sent++;
                _logger.LogInformation("Scheduled notification {Id} sent to {Recipients} users.", id, recipients);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled notification {Id} could not be sent.", id);
                await Finish(id, ScheduledNotificationStatus.Failed, null, "The message could not be sent. The server log has the details.", CancellationToken.None);
            }
        }

        return sent;
    }

    private Task Finish(Guid id, ScheduledNotificationStatus status, int? recipients, string? error, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        return _db.ScheduledNotifications
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, status)
                .SetProperty(s => s.Recipients, recipients)
                .SetProperty(s => s.Error, error)
                .SetProperty(s => s.SentAt, status == ScheduledNotificationStatus.Sent ? now : null)
                .SetProperty(s => s.UpdatedAt, now), ct);
    }

    // ---------------------------------------------------------------- shared

    private sealed record ValidMessage(string Title, string Body);

    private async Task<ValidMessage> ValidateAsync(
        string? rawTitle, string? rawBody, Guid? auctionId, ScheduledNotificationAudience audience, CancellationToken ct)
    {
        var title = (rawTitle ?? string.Empty).Trim();
        var body = (rawBody ?? string.Empty).Trim();

        if (title.Length == 0) Fail("Write a title.");
        if (title.Length > TitleMax) Fail($"The title can be at most {TitleMax} characters.");
        if (body.Length == 0) Fail("Write the message.");
        if (body.Length > BodyMax) Fail($"The message can be at most {BodyMax} characters.");
        if (!Enum.IsDefined(audience)) Fail("Choose who receives the message.");

        if (audience == ScheduledNotificationAudience.AuctionParticipants && auctionId is null)
        {
            Fail("Choose the auction whose participants receive the message.");
        }

        if (auctionId is { } id)
        {
            var status = await _db.Auctions.AsNoTracking()
                .Where(a => a.Id == id).Select(a => (AuctionStatus?)a.Status).FirstOrDefaultAsync(ct);

            if (status is null)
                throw new DomainException(ErrorCodes.AuctionNotFound, "Auction not found.");

            // A draft is invisible to users, so a link to it would open an error.
            if (status == AuctionStatus.Draft) Fail("Publish the auction before linking to it.");
        }

        return new ValidMessage(title, body);
    }

    private DateTime ValidateTime(DateTime sendAt)
    {
        var at = sendAt.Kind == DateTimeKind.Local ? sendAt.ToUniversalTime() : DateTime.SpecifyKind(sendAt, DateTimeKind.Utc);
        var now = _clock.UtcNow;

        if (at < now.AddMinutes(1)) Fail("Choose a time at least one minute from now.");
        if (at > now + MaxScheduleAhead) Fail($"A message can be scheduled at most {MaxScheduleAhead.TotalDays:0} days ahead.");
        return at;
    }

    private static NotificationTemplate Template(string title, string body, Guid? auctionId)
    {
        var data = new Dictionary<string, string> { ["type"] = "ADMIN_MESSAGE" };
        if (auctionId is { } id) data["auctionId"] = id.ToString();

        return new NotificationTemplate(
            NotificationType.AdminMessage,
            NotificationKeys.AdminMessageTitle,
            NotificationKeys.AdminMessageBody,
            new object[] { body },
            data,
            new object[] { title });
    }

    private Task<List<Guid>> ParticipantIdsAsync(Guid auctionId, CancellationToken ct) =>
        _db.AuctionParticipants.AsNoTracking()
            .Where(p => p.AuctionId == auctionId)
            .Select(p => p.UserId)
            .Distinct()
            .ToListAsync(ct);

    private async Task<ScheduledNotification> FindWaitingAsync(Guid id, CancellationToken ct)
    {
        var row = await _db.ScheduledNotifications.FirstOrDefaultAsync(s => s.Id == id, ct)
                  ?? throw new DomainException(ErrorCodes.NotFound, "Scheduled message not found.");

        if (row.Status != ScheduledNotificationStatus.Scheduled)
        {
            throw new DomainException(ErrorCodes.Conflict,
                row.Status == ScheduledNotificationStatus.Cancelled
                    ? "This message was cancelled."
                    : "This message has already gone out and can no longer be changed.");
        }

        return row;
    }

    private async Task SaveWaitingAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The worker picked it up in the meantime.
            throw new DomainException(ErrorCodes.Conflict, "This message has just started going out and can no longer be changed.");
        }
    }

    private async Task<ScheduledNotificationDto> GetAsync(Guid id, CancellationToken ct) =>
        await Rows(_db.ScheduledNotifications.AsNoTracking().Where(s => s.Id == id)).FirstAsync(ct);

    private IQueryable<ScheduledNotificationDto> Rows(IQueryable<ScheduledNotification> q) =>
        q.Select(s => new ScheduledNotificationDto(
            s.Id, s.Title, s.Body, s.AuctionId,
            s.AuctionId == null ? null : _db.Auctions.Where(a => a.Id == s.AuctionId).Select(a => a.Title).FirstOrDefault(),
            s.Audience, s.SendAt, s.Status, s.SentAt, s.Recipients, s.Error, s.CreatedAt));

    private Task Log(
        AuditActionType action, Guid? id, ValidMessage message, Guid? auctionId,
        ScheduledNotificationAudience audience, int? users, DateTime? sendAt, CancellationToken ct) =>
        _audit.LogAsync(action, id is null ? "Notification" : "ScheduledNotification", id ?? auctionId,
            null, new { title = message.Title, body = message.Body, AuctionId = auctionId, audience, users, sendAt }, ct: ct);

    private static void Fail(string message) =>
        throw new DomainException(ErrorCodes.ValidationFailed, message);
}
