using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Domain.Entities;

/// <summary>Append-only record of everything that happened to an auction.</summary>
public class AuctionEvent : Entity
{
    public Guid AuctionId { get; set; }
    public Auction? Auction { get; set; }

    public Guid? UserId { get; set; }
    public AuctionEventType EventType { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Metadata { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

/// <summary>Append-only record of administrative and financial operations.</summary>
public class AuditLog : Entity
{
    public AuditActionType Action { get; set; }

    public Guid? ActorUserId { get; set; }
    public string? ActorEmail { get; set; }

    /// <summary>Entity type and id the action targeted.</summary>
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    /// <summary>Mandatory justification for financial adjustments.</summary>
    public string? Reason { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Notification : Entity
{
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public NotificationType Type { get; set; }

    /// <summary>Localization key; the rendered title is resolved per user language.</summary>
    public string TitleKey { get; set; } = string.Empty;
    public string BodyKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>JSON payload delivered alongside the push, used for deep links.</summary>
    public string? Data { get; set; }

    public DateTime? ReadAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Single-row store configuration, editable by administrators.
/// </summary>
public class StoreSettings : AuditableEntity
{
    public bool StoreEnabled { get; set; } = true;
    public bool ShoppingWalletEnabled { get; set; } = true;
    public bool AllowShoppingWalletPurchases { get; set; } = true;
    public bool AllowMainWalletPurchases { get; set; } = true;

    public string DefaultCurrency { get; set; } = Money.DefaultCurrency;

    public bool TaxEnabled { get; set; }
    public decimal TaxPercentage { get; set; }

    public bool ShippingEnabled { get; set; } = true;
    public decimal DefaultShippingFee { get; set; }

    public decimal MinimumOrderAmount { get; set; }
    public decimal MaximumOrderAmount { get; set; } = 100000m;

    public bool ShoppingCreditExpirationEnabled { get; set; }
    public int ShoppingCreditExpirationDays { get; set; } = 365;

    public string? RefundPolicy { get; set; }
    public int LowStockThreshold { get; set; } = 5;
}
