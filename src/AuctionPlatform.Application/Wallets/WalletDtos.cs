using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Application.Wallets;

/// <summary>
/// The two balances are always reported separately. There is deliberately no combined total,
/// because presenting one would imply the whole amount is withdrawable.
/// </summary>
public sealed record WalletsDto(
    WalletDto Main,
    WalletDto Shopping,
    string Currency);

public sealed record WalletDto(
    Guid Id,
    WalletType WalletType,
    decimal Balance,
    string Currency,
    bool IsWithdrawable,
    DateTime UpdatedAt);

public sealed record WalletTransactionDto(
    Guid Id,
    WalletType WalletType,
    WalletTransactionType TransactionType,
    decimal Amount,
    string Currency,
    decimal BalanceBefore,
    decimal BalanceAfter,
    string? ReferenceType,
    Guid? ReferenceId,
    ShoppingCreditSource? CreditSource,
    DateTime? ExpiresAt,
    string? Description,
    DateTime CreatedAt);

public sealed class WalletTransactionQuery : PagedQuery
{
    public WalletType? WalletType { get; set; }
    public WalletTransactionType? TransactionType { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public sealed record DepositRequest(decimal Amount, string? Currency, string? Provider, string IdempotencyKey);

/// <summary>
/// A withdrawal. <paramref name="OtpCode"/> is required whenever
/// <c>Auction:RequireOtpForWithdrawal</c> is on, and the code must have been issued for this exact
/// amount, currency and destination.
/// </summary>
public sealed record WithdrawRequest(
    decimal Amount,
    string? Currency,
    string IdempotencyKey,
    string? Destination,
    string? OtpCode = null);

/// <summary>Ask for the code that confirms a specific withdrawal.</summary>
public sealed record RequestWithdrawalOtpRequest(decimal Amount, string? Currency, string? Destination);

public sealed record AdminCreditRequest(
    decimal Amount,
    string? Currency,
    WalletType WalletType,
    string Reason,
    string IdempotencyKey);

/// <summary>Result of a balance movement, including the ledger entry that recorded it.</summary>
public sealed record WalletOperationResult(
    Guid WalletId,
    WalletType WalletType,
    decimal BalanceBefore,
    decimal BalanceAfter,
    decimal Amount,
    string Currency,
    Guid TransactionId,
    bool WasDuplicate);

public sealed record ShoppingCreditSummaryDto(
    decimal TotalIssued,
    decimal TotalUsed,
    decimal Outstanding,
    string Currency);
