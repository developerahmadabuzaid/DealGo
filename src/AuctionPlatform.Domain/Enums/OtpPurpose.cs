namespace AuctionPlatform.Domain.Enums;

/// <summary>
/// What a one-time code may be used for.
///
/// Codes are scoped to a purpose so a code issued for signing in can never be replayed to
/// authorise a withdrawal, and vice versa.
/// </summary>
public enum OtpPurpose
{
    Login = 0,
    Withdrawal = 1,
    VerifyContact = 2,
    PrizePayment = 3
}

/// <summary>How a one-time code was delivered.</summary>
public enum OtpChannel
{
    Sms = 0,
    Email = 1
}
