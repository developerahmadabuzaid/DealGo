using System.Net.Http.Json;
using System.Text.Json;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Persistence;
using Google.Apis.Auth.OAuth2;
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
            // Staff may have reworded this message on the dashboard, or switched it off.
            var automatic = AutomaticMessages.ForTitleKey(template.TitleKey);
            var custom = automatic is null
                ? null
                : await _db.NotificationMessageSettings.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Code == automatic.Code, ct);
            if (custom is { Enabled: false }) return;

            var recipients = await _db.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id) && u.NotificationsEnabled)
                .Select(u => new { u.Id, u.Language })
                .ToListAsync(ct);

            if (recipients.Count == 0) return;

            var dataJson = template.Data is null ? null : JsonSerializer.Serialize(template.Data);
            var args = template.BodyArgs?.ToArray() ?? Array.Empty<object>();
            var titleArgs = template.TitleArgs?.ToArray() ?? Array.Empty<object>();
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
                    Title = Text(automatic, custom, recipient.Language, title: true, args)
                        ?? _localization.Translate(template.TitleKey, recipient.Language, titleArgs),
                    Body = Text(automatic, custom, recipient.Language, title: false, args)
                        ?? _localization.Translate(template.BodyKey, recipient.Language, args),
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

    /// <summary>
    /// Staff's wording for this recipient's language, filled in with this notification's values;
    /// null when they left it to the built-in text. The values the code passes are the body's,
    /// and staff may use them in the title too.
    /// </summary>
    private static string? Text(
        AutomaticMessageDefinition? automatic, NotificationMessageSetting? custom, string? language, bool title,
        object[] values)
    {
        if (automatic is null || custom is null) return null;

        var english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        var text = (english, title) switch
        {
            (true, true) => custom.TitleEn,
            (true, false) => custom.BodyEn,
            (false, true) => custom.TitleAr,
            (false, false) => custom.BodyAr
        };

        return text is null ? null : AutomaticMessages.Fill(text, automatic, values);
    }

    public async Task<int> NotifyEveryoneAsync(NotificationTemplate template, CancellationToken ct = default)
    {
        const int batch = 500;
        var sent = 0;
        Guid? after = null;

        // Keyset paging on the id, so accounts created meanwhile neither repeat nor shift a page.
        while (true)
        {
            var q = _db.Users.AsNoTracking().Where(u => u.IsActive && u.NotificationsEnabled);
            if (after is { } last) q = q.Where(u => u.Id.CompareTo(last) > 0);

            var ids = await q.OrderBy(u => u.Id).Select(u => u.Id).Take(batch).ToListAsync(ct);
            if (ids.Count == 0) break;

            await NotifyManyAsync(ids, template, ct);
            sent += ids.Count;
            after = ids[^1];

            if (ids.Count < batch) break;
        }

        return sent;
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

            var gone = new List<string>();

            foreach (var notification in notifications)
            {
                if (!byUser.TryGetValue(notification.UserId, out var deviceTokens)) continue;

                // The notification's own id rides along, so the phone can mark it read when tapped.
                var data = new Dictionary<string, string>(
                    template.Data ?? new Dictionary<string, string>())
                {
                    ["notificationId"] = notification.Id.ToString()
                };

                gone.AddRange(await _push.SendAsync(
                    deviceTokens, notification.Title, notification.Body, data, ct));
                notification.SentAt = _clock.UtcNow;
            }

            await _db.SaveChangesAsync(ct);

            // A phone that removed the app, or was given a new token, would otherwise be sent every
            // notification for ever and refused every time.
            if (gone.Count > 0)
            {
                await _db.UserDeviceTokens.Where(t => gone.Contains(t.Token)).ExecuteDeleteAsync(ct);
            }
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
/// Firebase Cloud Messaging sender (HTTP v1). Android phones are reached through Firebase directly
/// and iPhones through Firebase's link to Apple, so one sender covers both.
///
/// It signs in with the project's service account, which is a secret: it comes from configuration
/// (<c>Firebase__CredentialsJson</c> in web.config, or a file named by <c>Firebase__CredentialsPath</c>)
/// and never from the app. With nothing configured it logs what it would have sent, so local
/// development and the tests never need a Firebase project.
/// </summary>
public sealed class FirebasePushNotificationSender : IPushNotificationSender
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    /// <summary>The Android notification channel the app creates; see the app's push service.</summary>
    private const string AndroidChannel = "dealgo_alerts";

    private readonly HttpClient _http;
    private readonly FirebaseOptions _options;
    private readonly ILogger<FirebasePushNotificationSender> _logger;

    /*
     * The sender is created per request (a typed HttpClient), but the signed-in account is kept for
     * the life of the process, so its access token is reused for the hour it is valid instead of
     * being requested again for every notification.
     */
    private static readonly object AccountLock = new();
    private static (string Key, GoogleCredential? Credential, string? ProjectId)? _account;

    public FirebasePushNotificationSender(
        HttpClient http,
        Microsoft.Extensions.Options.IOptions<FirebaseOptions> options,
        ILogger<FirebasePushNotificationSender> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<string>> SendAsync(
        IReadOnlyCollection<string> deviceTokens,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken cancellationToken = default)
    {
        if (deviceTokens.Count == 0) return Array.Empty<string>();

        var (credential, projectId) = _options.Enabled ? Account() : (null, null);
        if (credential is null || string.IsNullOrWhiteSpace(projectId))
        {
            _logger.LogInformation(
                "[push disabled] {Title} -> {Count} device(s): {Body}", title, deviceTokens.Count, body);
            return Array.Empty<string>();
        }

        string accessToken;
        try
        {
            // Cached by the credential and renewed shortly before it expires.
            accessToken = await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not sign in to Firebase; push skipped.");
            return Array.Empty<string>();
        }

        var url = $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send";
        var gone = new List<string>();

        foreach (var token in deviceTokens)
        {
            var payload = new
            {
                message = new
                {
                    token,
                    notification = new { title, body },
                    data = data ?? new Dictionary<string, string>(),
                    android = new
                    {
                        priority = "high",
                        notification = new { channel_id = AndroidChannel, sound = "default" }
                    },
                    apns = new
                    {
                        payload = new { aps = new { sound = "default" } }
                    }
                }
            };

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(payload)
                };
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                using var response = await _http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode) continue;

                var error = await response.Content.ReadAsStringAsync(cancellationToken);

                // 404 UNREGISTERED: the app was removed or the token replaced. A 400 naming the
                // token as invalid means it was never one. Either way it will never work again.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound
                    || error.Contains("UNREGISTERED", StringComparison.Ordinal)
                    || (response.StatusCode == System.Net.HttpStatusCode.BadRequest
                        && error.Contains("registration token", StringComparison.OrdinalIgnoreCase)))
                {
                    gone.Add(token);
                    continue;
                }

                _logger.LogWarning("FCM rejected a message with {Status}.", response.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "FCM delivery failed for one device token.");
            }
        }

        return gone;
    }

    private (GoogleCredential?, string?) Account()
    {
        var key = $"{_options.CredentialsPath}|{_options.ProjectId}|{_options.CredentialsJson?.GetHashCode()}";
        lock (AccountLock)
        {
            if (_account is { } cached && cached.Key == key) return (cached.Credential, cached.ProjectId);
            var (credential, projectId) = LoadAccount();
            _account = (key, credential, projectId);
            return (credential, projectId);
        }
    }

    private (GoogleCredential?, string?) LoadAccount()
    {
        try
        {
            var json = _options.CredentialsJson;
            if (string.IsNullOrWhiteSpace(json) && !string.IsNullOrWhiteSpace(_options.CredentialsPath)
                && File.Exists(_options.CredentialsPath))
            {
                json = File.ReadAllText(_options.CredentialsPath);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("Firebase is enabled but no service account is configured; push is off.");
                return (null, null);
            }

            var projectId = _options.ProjectId;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                using var doc = JsonDocument.Parse(json);
                projectId = doc.RootElement.TryGetProperty("project_id", out var p) ? p.GetString() : null;
            }

            var credential = CredentialFactory.FromJson<ServiceAccountCredential>(json)
                .ToGoogleCredential()
                .CreateScoped(Scope);

            return (credential, projectId);
        }
        catch (Exception ex)
        {
            // Never the contents: the file holds a private key.
            _logger.LogError("Firebase service account could not be read ({Error}); push is off.", ex.GetType().Name);
            return (null, null);
        }
    }
}
