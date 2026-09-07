using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Domain.Entities;

/// <summary>
/// A user balance. Every user has exactly one Main and one Shopping wallet.
/// UNIQUE(UserId, WalletType).
/// </summary>
public class Wallet : AuditableEntity
{
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public WalletType WalletType { get; set; }

    /// <summary>numeric(18,2). Never mutated without a matching WalletTransaction.</summary>
    public decimal Balance { get; set; }

    public string Currency { get; set; } = Money.DefaultCurrency;

    /// <summary>Optimistic concurrency token mapped to the PostgreSQL xmin system column.</summary>
    public uint Version { get; set; }

    public ICollection<WalletTransaction> Transactions { get; set; } = new List<WalletTransaction>();
}

/// <summary>
/// Append-only ledger entry. Balance changes are only ever legal alongside one of these.
/// </summary>
public class WalletTransaction : Entity
{
    public Guid WalletId { get; set; }
    public Wallet? Wallet { get; set; }

    public Guid UserId { get; set; }
    public WalletType WalletType { get; set; }
    public WalletTransactionType TransactionType { get; set; }

    /// <summary>Signed amount: positive credits the wallet, negative debits it.</summary>
    public decimal Amount { get; set; }

    public string Currency { get; set; } = Money.DefaultCurrency;

    public decimal BalanceBefore { get; set; }
    public decimal BalanceAfter { get; set; }

    /// <summary>Free-form pointer to the originating record, e.g. "Auction", "StoreOrder".</summary>
    public string? ReferenceType { get; set; }
    public Guid? ReferenceId { get; set; }

    /// <summary>For shopping-wallet credits, records why the credit exists.</summary>
    public ShoppingCreditSource? CreditSource { get; set; }

    /// <summary>Set when shopping-credit expiry is enabled.</summary>
    public DateTime? ExpiresAt { get; set; }

    public string? Description { get; set; }
    public string? Metadata { get; set; }

    /// <summary>Deduplicates retried financial commands. UNIQUE when present.</summary>
    public string? IdempotencyKey { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
