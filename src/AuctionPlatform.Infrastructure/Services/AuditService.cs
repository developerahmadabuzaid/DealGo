using System.Text.Json;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// Writes the append-only administrative audit trail. Audit failures are logged but never abort
/// the operation being audited, because a lost log line is preferable to a half-applied refund.
/// </summary>
public sealed class AuditService : IAuditService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ApplicationDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<AuditService> _logger;

    public AuditService(
        ApplicationDbContext db,
        ICurrentUserService currentUser,
        IDateTimeProvider clock,
        ILogger<AuditService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;
    }

    public async Task LogAsync(
        AuditActionType action,
        string entityType,
        Guid? entityId,
        object? oldValue = null,
        object? newValue = null,
        string? reason = null,
        CancellationToken ct = default)
    {
        try
        {
            _db.AuditLogs.Add(new AuditLog
            {
                Action = action,
                ActorUserId = _currentUser.UserId,
                ActorEmail = _currentUser.Email,
                EntityType = entityType,
                EntityId = entityId,
                OldValue = Serialize(oldValue),
                NewValue = Serialize(newValue),
                Reason = reason,
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                CreatedAt = _clock.UtcNow
            });

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Audit: {Action} on {EntityType} {EntityId} by {Actor}{Reason}",
                action, entityType, entityId, _currentUser.Email ?? "system",
                reason is null ? string.Empty : $" ({reason})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit record for {Action} on {EntityType}.", action, entityType);
        }
    }

    private static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
}

/// <summary>Allocates the customer-facing AUC-/ORD- numbers from PostgreSQL sequences.</summary>
public sealed class NumberGenerator : INumberGenerator
{
    private readonly ApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;

    public NumberGenerator(ApplicationDbContext db, IDateTimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public Task<string> NextAuctionNumberAsync(CancellationToken ct = default) =>
        NextAsync("AuctionNumberSeq", "AUC", ct);

    public Task<string> NextOrderNumberAsync(CancellationToken ct = default) =>
        NextAsync("OrderNumberSeq", "ORD", ct);

    private async Task<string> NextAsync(string sequence, string prefix, CancellationToken ct)
    {
        await using var command = _db.Database.GetDbConnection().CreateCommand();

        var connection = _db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        command.CommandText = $"SELECT nextval('\"{sequence}\"')";
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();

        var result = await command.ExecuteScalarAsync(ct);
        var value = Convert.ToInt64(result);

        return $"{prefix}-{_clock.UtcNow:yyyy}-{value:D6}";
    }
}
