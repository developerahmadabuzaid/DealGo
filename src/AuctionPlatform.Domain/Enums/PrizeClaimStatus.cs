namespace AuctionPlatform.Domain.Enums;

public enum PrizeClaimStatus
{
    Pending = 0,
    PaymentRequired = 1,
    PaymentProcessing = 2,
    Paid = 3,
    Claimed = 4,
    Fulfilled = 5,
    Cancelled = 6,
    Expired = 7
}
