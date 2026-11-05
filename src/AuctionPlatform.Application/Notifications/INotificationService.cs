using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Application.Notifications;

/// <summary>
/// Persists an in-app notification and pushes it to the user's devices. Delivery is best-effort
/// and never participates in a financial transaction: a failed push must not roll back a refund.
/// </summary>
public interface INotificationService
{
    Task NotifyAsync(NotificationRequest request, CancellationToken ct = default);

    Task NotifyManyAsync(IReadOnlyCollection<Guid> userIds, NotificationTemplate template, CancellationToken ct = default);

    /// <summary>
    /// Sends to every active account that has notifications on, a batch at a time. Slow for a large
    /// audience, so callers in a request go through <see cref="INotificationBroadcaster"/>.
    /// Returns how many accounts it was written for.
    /// </summary>
    Task<int> NotifyEveryoneAsync(NotificationTemplate template, CancellationToken ct = default);

    Task<PagedResult<NotificationDto>> GetForUserAsync(Guid userId, PagedQuery query, CancellationToken ct = default);

    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken ct = default);

    Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default);

    Task MarkAllReadAsync(Guid userId, CancellationToken ct = default);

    Task RegisterDeviceAsync(Guid userId, string token, string? platform, CancellationToken ct = default);

    Task UnregisterDeviceAsync(Guid userId, string token, CancellationToken ct = default);
}

public sealed record NotificationRequest(Guid UserId, NotificationTemplate Template);

/// <summary>
/// A notification described by localization keys plus arguments, so the body is rendered in each
/// recipient's own language rather than baked in at the call site.
/// </summary>
public sealed record NotificationTemplate(
    NotificationType Type,
    string TitleKey,
    string BodyKey,
    IReadOnlyList<object>? BodyArgs = null,
    IReadOnlyDictionary<string, string>? Data = null,
    IReadOnlyList<object>? TitleArgs = null);

/// <summary>
/// Hands a notification for everyone to a background worker, so the request that caused it (an
/// admin publishing an auction, or sending a message) returns at once.
/// </summary>
public interface INotificationBroadcaster
{
    ValueTask EnqueueAsync(NotificationTemplate template, CancellationToken ct = default);
}

public sealed record NotificationDto(
    Guid Id,
    NotificationType Type,
    string Title,
    string Body,
    string? Data,
    DateTime? ReadAt,
    DateTime CreatedAt);
