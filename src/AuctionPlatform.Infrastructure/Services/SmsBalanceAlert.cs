using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// The state lives in rows that exist anyway, so it survives a restart of the site: an audit row
/// for each alert, and the code rows that show Tawked last accepted a message. A code sent after
/// the last alert means the balance was topped up.
/// </summary>
public sealed class SmsBalanceAlert : ISmsBalanceAlert
{
    /// <summary>Often enough not to be forgotten; every failed sign-in would be far too often.</summary>
    public static readonly TimeSpan RepeatEvery = TimeSpan.FromHours(6);

    /// <summary>Only these roles can do anything about the balance.</summary>
    private static readonly string[] RecipientRoles = { Roles.SuperAdmin, Roles.Admin };

    private readonly ApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly INotificationService _notifications;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<SmsBalanceAlert> _logger;

    public SmsBalanceAlert(
        ApplicationDbContext db,
        IAuditService audit,
        INotificationService notifications,
        IDateTimeProvider clock,
        ILogger<SmsBalanceAlert> logger)
    {
        _db = db;
        _audit = audit;
        _notifications = notifications;
        _clock = clock;
        _logger = logger;
    }

    public async Task RaiseAsync(CancellationToken ct = default)
    {
        try
        {
            var alerts = await AlertsSinceLastSendAsync(ct);
            var last = await alerts.MaxAsync(a => (DateTime?)a.CreatedAt, ct);

            if (last is { } at && _clock.UtcNow - at < RepeatEvery) return;

            await _audit.LogAsync(
                AuditActionType.SmsBalanceEmpty, "Tawked", null,
                reason: "Tawked refused a code: no balance left.", ct: ct);

            var admins = await (
                    from userRole in _db.UserRoles
                    join role in _db.Roles on userRole.RoleId equals role.Id
                    join user in _db.Users on userRole.UserId equals user.Id
                    where RecipientRoles.Contains(role.Name!) && user.IsActive
                    select user.Id)
                .Distinct()
                .ToListAsync(ct);

            await _notifications.NotifyManyAsync(
                admins,
                new NotificationTemplate(
                    NotificationType.Generic,
                    NotificationKeys.SmsBalanceEmptyTitle,
                    NotificationKeys.SmsBalanceEmptyBody,
                    Data: new Dictionary<string, string> { ["type"] = "SMS_BALANCE_EMPTY" }),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not tell staff that the SMS balance is empty.");
        }
    }

    public async Task<DateTime?> GetEmptySinceAsync(CancellationToken ct = default)
    {
        var alerts = await AlertsSinceLastSendAsync(ct);
        return await alerts.MinAsync(a => (DateTime?)a.CreatedAt, ct);
    }

    private async Task<IQueryable<AuditLog>> AlertsSinceLastSendAsync(CancellationToken ct)
    {
        var lastSend = await _db.OtpCodes.AsNoTracking()
            .Where(o => o.ProviderVerificationId != null)
            .MaxAsync(o => (DateTime?)o.CreatedAt, ct);

        var alerts = _db.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActionType.SmsBalanceEmpty);

        return lastSend is { } sent ? alerts.Where(a => a.CreatedAt > sent) : alerts;
    }
}
