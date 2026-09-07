namespace AuctionPlatform.Domain.Enums;

/// <summary>What happens when the maximum duration elapses before the target price is reached.</summary>
public enum ExpirationPolicy
{
    /// <summary>Finish the auction and rank/reward whoever participated. Recommended default.</summary>
    EndAndDistribute = 0,
    Extend = 1,
    Cancel = 2,
    Restart = 3
}
