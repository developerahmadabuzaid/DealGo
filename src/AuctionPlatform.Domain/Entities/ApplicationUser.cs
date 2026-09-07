using Microsoft.AspNetCore.Identity;

namespace AuctionPlatform.Domain.Entities;

/// <summary>Platform user. Identity is keyed by Guid to match the rest of the schema.</summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;

    /// <summary>Preferred UI language: "ar" (default) or "en".</summary>
    public string Language { get; set; } = "ar";

    public bool IsActive { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Firebase Cloud Messaging device tokens for push delivery.</summary>
    public ICollection<UserDeviceToken> DeviceTokens { get; set; } = new List<UserDeviceToken>();

    public ICollection<Wallet> Wallets { get; set; } = new List<Wallet>();
    public ICollection<ShippingAddress> Addresses { get; set; } = new List<ShippingAddress>();
}

public class ApplicationRole : IdentityRole<Guid>
{
    public string? Description { get; set; }
    public ApplicationRole() { }
    public ApplicationRole(string name) : base(name) { }
}

public class UserDeviceToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }
    public string Token { get; set; } = string.Empty;
    public string? Platform { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A single issued refresh token. Rotation replaces the token on every use and records the
/// replacement, so replaying a consumed token can be detected and the family revoked.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>SHA-256 hash of the token. The raw value is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? UsedAt { get; set; }

    public Guid? ReplacedByTokenId { get; set; }
    public string? RevokedReason { get; set; }

    public string? CreatedByIp { get; set; }
    public string? UserAgent { get; set; }

    public bool IsActive => RevokedAt is null && UsedAt is null && DateTime.UtcNow < ExpiresAt;
}
