using System.Net.Http.Json;
using System.Text.Json;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// Stores the in-app notification and pushes to registered devices.
///
/// Push delivery is fire-and-forget by design: a notification is never allowed to decide whether a
/// financial transaction succeeded.
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly ApplicationDbContext _db;
    private readonly ILocalizationService _localization;
    private readonly IPushNotificationSender _push;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        ApplicationDbContext db,
        ILocalizationService localization,
        IPushNotificationSender push,
        IDateTimeProvider clock,
        ILogger<NotificationService> logger)
    {
        _db = db;
        _localization = localization;
        _push = push;
        _clock = clock;
        _logger = logger;
    }

    public Task NotifyAsync(NotificationRequest request, CancellationToken ct = default) =>
        NotifyManyAsync(new[] { request.UserId }, request.Template, ct);

    public async Task NotifyManyAsync(
        IReadOnlyCollection<Guid> userIds, NotificationTemplate template, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return;

        try
        {
            var recipients = await _db.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id) && u.NotificationsEnabled)
                .Select(u => new { u.Id, u.Language })
                .ToListAsync(ct);

            if (recipients.Count == 0) return;

            var dataJson = template.Data is null ? null : JsonSerializer.Serialize(template.Data);
            var args = template.BodyArgs?.ToArray() ?? Array.Empty<object>();
            var now = _clock.UtcNow;

            var notifications = new List<Notification>(recipients.Count);

            foreach (var recipient in recipients)
            {
                notifications.Add(new Notification
                {
                    UserId = recipient.Id,
                    Type = template.Type,
                    TitleKey = template.TitleKey,
                    BodyKey = template.BodyKey,
                    Title = _localization.Translate(template.TitleKey, recipient.Language),
                    Body = _localization.Translate(template.BodyKey, recipient.Language, args),
                    Data = dataJson,
                    CreatedAt = now
                });
            }

            _db.Notifications.AddRange(notifications);
            await _db.SaveChangesAsync(ct);

            await PushAsync(notifications, template, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deliver {Type} notification to {Count} users.",
                template.Type, userIds.Count);
        }
    }

    private async Task PushAsync(
        List<Notification> notifications, NotificationTemplate template, CancellationToken ct)
    {
        try
        {
            var userIds = notifications.Select(n => n.UserId).ToList();

            var tokens = await _db.UserDeviceTokens
                .AsNoTracking()
                .Where(t => userIds.Contains(t.UserId))
                .Select(t => new { t.UserId, t.Token })
                .ToListAsync(ct);

            if (tokens.Count == 0) return;

            var byUser = tokens.GroupBy(t => t.UserId)
                .ToDictionary(g => g.Key, g => g.Select(t => t.Token).ToList());

            foreach (var notification in notifications)
            {
                if (!byUser.TryGetValue(notification.UserId, out var deviceTokens)) continue;

                await _push.SendAsync(deviceTokens, notification.Title, notification.Body, template.Data, ct);
                notification.SentAt = _clock.UtcNow;
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push delivery failed; in-app notifications were still stored.");
        }
    }

    public async Task<PagedResult<NotificationDto>> GetForUserAsync(
        Guid userId, PagedQuery query, CancellationToken ct = default)
    {
        var q = _db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        var total = await q.LongCountAsync(ct);

        var items = await q
            .OrderByDescending(n => n.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, n.Data, n.ReadAt, n.CreatedAt))
            .ToListAsync(ct);

        return PagedResult<NotificationDto>.Create(items, query.Page, query.PageSize, total);
    }

    public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken ct = default) =>
        _db.Notifications.AsNoTracking().CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);

    public async Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        await _db.Notifications
            .Where(n => n.Id == notificationId && n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, _clock.UtcNow), ct);
    }

    public async Task MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        await _db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, _clock.UtcNow), ct);
    }

    public async Task RegisterDeviceAsync(Guid userId, string token, string? platform, CancellationToken ct = default)
    {
        var existing = await _db.UserDeviceTokens.FirstOrDefaultAsync(t => t.Token == token, ct);

        if (existing is not null)
        {
            // A device can move between accounts; the token follows the current user.
            existing.UserId = userId;
            existing.Platform = platform ?? existing.Platform;
            existing.LastSeenAt = _clock.UtcNow;
        }
        else
        {
            _db.UserDeviceTokens.Add(new UserDeviceToken
            {
                UserId = userId,
                Token = token,
                Platform = platform,
                CreatedAt = _clock.UtcNow,
                LastSeenAt = _clock.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task UnregisterDeviceAsync(Guid userId, string token, CancellationToken ct = default)
    {
        await _db.UserDeviceTokens
            .Where(t => t.UserId == userId && t.Token == token)
            .ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// Firebase Cloud Messaging sender. When Firebase credentials are not configured the sender logs
/// what it would have sent, so local development and tests never need a Firebase project.
/// </summary>
public sealed class FirebasePushNotificationSender : IPushNotificationSender
{
    private readonly HttpClient _http;
    private readonly FirebaseOptions _options;
    private readonly ILogger<FirebasePushNotificationSender> _logger;

    public FirebasePushNotificationSender(
        HttpClient http,
        Microsoft.Extensions.Options.IOptions<FirebaseOptions> options,
        ILogger<FirebasePushNotificationSender> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(
        IReadOnlyCollection<string> deviceTokens,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken cancellationToken = default)
    {
        if (deviceTokens.Count == 0) return;

        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ProjectId))
        {
            _logger.LogInformation(
                "[push disabled] {Title} -> {Count} device(s): {Body}", title, deviceTokens.Count, body);
            return;
        }

        // The FCM HTTP v1 API requires an OAuth2 access token minted from the service account.
        // Wiring a real credential provider is a deployment concern; the request shape is here so
        // the integration point is unambiguous.
        foreach (var token in deviceTokens)
        {
            var payload = new
            {
                message = new
                {
                    token,
                    notification = new { title, body },
                    data = data ?? new Dictionary<string, string>()
                }
            };

            try
            {
                var response = await _http.PostAsJsonAsync(
                    $"https://fcm.googleapis.com/v1/projects/{_options.ProjectId}/messages:send",
                    payload, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "FCM rejected a message with {Status}.", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FCM delivery failed for one device token.");
            }
        }
    }
}
