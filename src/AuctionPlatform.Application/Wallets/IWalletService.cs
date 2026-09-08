using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Application.Wallets;

/// <summary>
/// The only sanctioned path to a balance change. Every credit and debit writes a ledger entry in
/// the same transaction as the balance update, and the wallet row is locked for the duration.
/// </summary>
public interface IWalletService
{
    Task<WalletsDto> GetWalletsAsync(Guid userId, CancellationToken ct = default);

    Task EnsureWalletsExistAsync(Guid userId, string currency, CancellationToken ct = default);

    /// <summary>
    /// Adds funds. Must be called inside an open transaction when it is part of a larger
    /// operation; it locks the wallet row and will not commit on its own.
    /// </summary>
    Task<WalletOperationResult> CreditAsync(WalletMovement movement, CancellationToken ct = default);

    /// <summary>Removes funds, failing with INSUFFICIENT_BALANCE rather than going negative.</summary>
    Task<WalletOperationResult> DebitAsync(WalletMovement movement, CancellationToken ct = default);

    Task<PagedResult<WalletTransactionDto>> GetTransactionsAsync(
        Guid userId, WalletTransactionQuery query, CancellationToken ct = default);

    Task<WalletOperationResult> DepositAsync(Guid userId, DepositRequest request, CancellationToken ct = default);

    /// <summary>
    /// Sends the one-time code that authorises a specific withdrawal. The code is bound to the
    /// amount, currency and destination, so it cannot approve a different transfer.
    /// </summary>
    Task<Identity.OtpChallengeDto> RequestWithdrawalOtpAsync(
        Guid userId, RequestWithdrawalOtpRequest request, CancellationToken ct = default);

    /// <summary>
    /// Withdraws from the Main wallet. Requires a matching one-time code when withdrawal OTP is
    /// enabled; shopping credit is never withdrawable regardless.
    /// </summary>
    Task<WalletOperationResult> WithdrawAsync(Guid userId, WithdrawRequest request, CancellationToken ct = default);

    Task<ShoppingCreditSummaryDto> GetShoppingCreditSummaryAsync(string currency, CancellationToken ct = default);
}

/// <summary>Describes one balance movement. Amount is always positive; direction is the method.</summary>
public sealed record WalletMovement
{
    public required Guid UserId { get; init; }
    public required WalletType WalletType { get; init; }
    public required decimal Amount { get; init; }
    public required WalletTransactionType TransactionType { get; init; }
    public string Currency { get; init; } = Domain.Common.Money.DefaultCurrency;

    public string? ReferenceType { get; init; }
    public Guid? ReferenceId { get; init; }
    public string? Description { get; init; }
    public string? Metadata { get; init; }

    /// <summary>When set, a repeat of the same key returns the original result instead of moving money again.</summary>
    public string? IdempotencyKey { get; init; }

    public ShoppingCreditSource? CreditSource { get; init; }
    public DateTime? ExpiresAt { get; init; }
}
