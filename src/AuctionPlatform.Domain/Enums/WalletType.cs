namespace AuctionPlatform.Domain.Enums;

public enum WalletType
{
    /// <summary>Cash-like balance. Deposits, withdrawals, entry fees and prize payments.</summary>
    Main = 0,

    /// <summary>Restricted store credit. Never withdrawable, transferable or convertible.</summary>
    Shopping = 1
}
