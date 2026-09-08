using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Domain.Entities;

/// <summary>
/// A one-time code issued to a destination for a single purpose.
///
/// Only the hash is stored, the same way refresh tokens are handled: a database leak yields no
/// usable codes. A code is single-use, expires quickly, is scoped to one purpose, and is retired
/// after a small number of wrong guesses so it cannot be brute-forced.
/// </summary>
public class OtpCode : Entity
{
    /// <summary>The normalised email address or phone number the code was sent to.</summary>
    public string Destination { get; set; } = string.Empty;

    public OtpChannel Channel { get; set; }
    public OtpPurpose Purpose { get; set; }

    /// <summary>Set when the code belongs to a known account. Null before the user is resolved.</summary>
    public Guid? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>SHA-256 of the code plus its salt. The code itself is never stored.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }

    /// <summary>Wrong guesses so far. The code is dead once this reaches the configured maximum.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Set when the code is retired early: superseded, too many attempts, or cancelled.</summary>
    public DateTime? InvalidatedAt { get; set; }
    public string? InvalidatedReason { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>
    /// Binds the code to what it authorises, so a withdrawal code approves that withdrawal only.
    /// For a withdrawal this is a hash of the amount, currency and destination.
    /// </summary>
    public string? OperationBinding { get; set; }

    public bool IsUsable(DateTime now, int maxAttempts) =>
        ConsumedAt is null &&
        InvalidatedAt is null &&
        AttemptCount < maxAttempts &&
        now < ExpiresAt;
}
