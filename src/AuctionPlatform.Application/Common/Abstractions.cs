using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Application.Common;

/// <summary>The authenticated caller, resolved from the JWT by the API layer.</summary>
public interface ICurrentUserService
{
    Guid? UserId { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
    IReadOnlyCollection<string> Roles { get; }
    IReadOnlyCollection<string> PermissionClaims { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
    bool HasPermission(string permission);
}

/// <summary>
/// The single source of "now". Everything time-dependent goes through this so tests can control
/// the clock and so no code path is tempted to read a client-supplied timestamp.
/// </summary>
public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}

public sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Distributed lock used to keep multiple API instances from duplicating scheduled work.</summary>
public interface IDistributedLockService
{
    /// <summary>Returns null when the lock is already held elsewhere.</summary>
    Task<IAsyncDisposable?> AcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default);
}

/// <summary>Redis-backed counters used for bid rate limiting.</summary>
public interface IRateLimiterService
{
    Task<bool> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default);
}

/// <summary>Writes audit records for administrative and financial operations.</summary>
public interface IAuditService
{
    Task LogAsync(
        AuditActionType action,
        string entityType,
        Guid? entityId,
        object? oldValue = null,
        object? newValue = null,
        string? reason = null,
        CancellationToken ct = default);
}

/// <summary>Push delivery. Failure here must never affect whether a transaction committed.</summary>
public interface IPushNotificationSender
{
    Task SendAsync(
        IReadOnlyCollection<string> deviceTokens,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves localized user-facing strings for notifications.</summary>
public interface ILocalizationService
{
    string Translate(string key, string language, params object[] args);
}

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default);
}
