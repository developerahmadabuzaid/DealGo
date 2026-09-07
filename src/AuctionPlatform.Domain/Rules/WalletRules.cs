using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Domain.Rules;

/// <summary>
/// Restrictions that separate cash from store credit. The Shopping wallet is a closed loop:
/// it enters through auction refunds and admin credits, and leaves only through store purchases.
/// </summary>
public static class WalletRules
{
    /// <summary>Operations that are legal on the Main wallet but never on the Shopping wallet.</summary>
    public static bool IsWithdrawable(WalletType walletType) => walletType == WalletType.Main;

    public static bool IsTransferable(WalletType walletType) => false;

    public static bool IsConvertible(WalletType walletType) => false;

    public static void EnsureWithdrawable(WalletType walletType)
    {
        if (!IsWithdrawable(walletType))
        {
            throw new DomainException(
                ErrorCodes.ShoppingWalletOperationNotAllowed,
                "Shopping Wallet balance can only be used for store purchases.");
        }
    }

    /// <summary>The Shopping wallet is deliberately excluded from any withdrawable total.</summary>
    public static decimal WithdrawableBalance(decimal mainBalance) => mainBalance;

    public static void EnsurePositiveAmount(decimal amount)
    {
        if (amount <= 0)
        {
            throw new DomainException(ErrorCodes.InvalidAmount, "Amount must be greater than zero.");
        }
    }

    public static void EnsureSufficientBalance(decimal balance, decimal amount, WalletType walletType)
    {
        if (balance < amount)
        {
            var code = walletType == WalletType.Shopping
                ? ErrorCodes.InsufficientShoppingCredit
                : ErrorCodes.InsufficientBalance;

            throw new DomainException(code,
                walletType == WalletType.Shopping
                    ? "Insufficient shopping credit for this purchase."
                    : "Insufficient balance for this operation.");
        }
    }

    public static void EnsureSameCurrency(string walletCurrency, string operationCurrency)
    {
        if (!string.Equals(walletCurrency, operationCurrency, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(ErrorCodes.CurrencyMismatch,
                $"Wallet currency {walletCurrency} does not match operation currency {operationCurrency}.");
        }
    }

    /// <summary>Which wallet a store product may be paid from.</summary>
    public static bool AcceptsPayment(StorePaymentMode mode, StorePaymentMethod method) => mode switch
    {
        StorePaymentMode.Both => true,
        StorePaymentMode.ShoppingWallet => method == StorePaymentMethod.ShoppingWallet,
        StorePaymentMode.MainWallet => method == StorePaymentMethod.MainWallet,
        _ => false
    };

    public static WalletType ToWalletType(StorePaymentMethod method) =>
        method == StorePaymentMethod.MainWallet ? WalletType.Main : WalletType.Shopping;
}
