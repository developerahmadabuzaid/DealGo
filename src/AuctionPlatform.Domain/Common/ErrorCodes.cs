namespace AuctionPlatform.Domain.Common;

/// <summary>
/// Stable, machine-readable error codes. These are part of the public API contract:
/// the mobile client branches on them, so values must not change once shipped.
/// </summary>
public static class ErrorCodes
{
    // Auction
    public const string AuctionNotFound = "AUCTION_NOT_FOUND";
    public const string AuctionNotActive = "AUCTION_NOT_ACTIVE";
    public const string AuctionFinished = "AUCTION_FINISHED";
    public const string AuctionNotReady = "AUCTION_NOT_READY";
    public const string AuctionNotPublishable = "AUCTION_NOT_PUBLISHABLE";
    public const string InvalidAuctionState = "INVALID_AUCTION_STATE";
    public const string InvalidAuctionConfiguration = "INVALID_AUCTION_CONFIGURATION";
    public const string RegistrationClosed = "REGISTRATION_CLOSED";
    public const string AuctionFull = "AUCTION_FULL";

    // Participation
    public const string ParticipantNotFound = "PARTICIPANT_NOT_FOUND";
    public const string AlreadyJoined = "ALREADY_JOINED";
    public const string EntryPaymentRequired = "ENTRY_PAYMENT_REQUIRED";

    // Bidding
    public const string BidNotAllowed = "BID_NOT_ALLOWED";
    public const string ConsecutiveBidNotAllowed = "CONSECUTIVE_BID_NOT_ALLOWED";
    public const string TargetAlreadyReached = "TARGET_ALREADY_REACHED";
    public const string DuplicateRequest = "DUPLICATE_REQUEST";
    public const string RateLimited = "RATE_LIMITED";

    // Wallet
    public const string InsufficientBalance = "INSUFFICIENT_BALANCE";
    public const string InsufficientShoppingCredit = "INSUFFICIENT_SHOPPING_CREDIT";
    public const string ShoppingWalletOperationNotAllowed = "SHOPPING_WALLET_OPERATION_NOT_ALLOWED";
    public const string WalletNotFound = "WALLET_NOT_FOUND";
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";
    public const string InvalidAmount = "INVALID_AMOUNT";

    // Store
    public const string ProductNotFound = "PRODUCT_NOT_FOUND";
    public const string ProductNotAvailable = "PRODUCT_NOT_AVAILABLE";
    public const string OutOfStock = "OUT_OF_STOCK";
    public const string PaymentMethodNotAccepted = "PAYMENT_METHOD_NOT_ACCEPTED";
    public const string StoreDisabled = "STORE_DISABLED";
    public const string OrderNotFound = "ORDER_NOT_FOUND";
    public const string OrderNotCancellable = "ORDER_NOT_CANCELLABLE";
    public const string InvalidOrderState = "INVALID_ORDER_STATE";
    public const string OrderAmountOutOfRange = "ORDER_AMOUNT_OUT_OF_RANGE";
    public const string CategoryNotFound = "CATEGORY_NOT_FOUND";

    /// <summary>Departments go two levels deep: main, then sub. Never a third.</summary>
    public const string CategoryTooDeep = "CATEGORY_TOO_DEEP";
    public const string CategoryHasSubcategories = "CATEGORY_HAS_SUBCATEGORIES";
    public const string DuplicateSku = "DUPLICATE_SKU";

    // Prizes
    public const string PrizeClaimNotFound = "PRIZE_CLAIM_NOT_FOUND";
    public const string PrizeClaimNotPayable = "PRIZE_CLAIM_NOT_PAYABLE";
    public const string PrizeClaimNotClaimable = "PRIZE_CLAIM_NOT_CLAIMABLE";

    // Auth / access
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string InvalidRefreshToken = "INVALID_REFRESH_TOKEN";
    public const string EmailAlreadyRegistered = "EMAIL_ALREADY_REGISTERED";
    public const string AccountDisabled = "ACCOUNT_DISABLED";
    public const string InvalidOtp = "INVALID_OTP";
    public const string OtpExpired = "OTP_EXPIRED";
    public const string OtpRequired = "OTP_REQUIRED";
    public const string OtpAlreadyUsed = "OTP_ALREADY_USED";
    public const string OtpTooManyAttempts = "OTP_TOO_MANY_ATTEMPTS";
    public const string OtpRequestTooSoon = "OTP_REQUEST_TOO_SOON";
    public const string OtpDestinationInvalid = "OTP_DESTINATION_INVALID";
    public const string PhoneNumberInvalid = "PHONE_NUMBER_INVALID";
    public const string PhoneNumberTaken = "PHONE_NUMBER_TAKEN";

    // Generic
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string InternalError = "INTERNAL_ERROR";

    /// <summary>The platform is up but something it depends on — the database — is not answering.</summary>
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
}
