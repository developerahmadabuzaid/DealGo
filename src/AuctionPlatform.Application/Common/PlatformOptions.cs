using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Domain.Rules;

namespace AuctionPlatform.Application.Common;

/// <summary>Platform-wide defaults an administrator can change through configuration.</summary>
public sealed class AuctionOptions
{
    public const string SectionName = "Auction";

    public decimal DefaultEntryFee { get; set; } = AuctionRules.DefaultEntryFee;
    public int DefaultRequiredParticipants { get; set; } = AuctionRules.DefaultRequiredParticipants;
    public int DefaultStartDelaySeconds { get; set; } = AuctionRules.DefaultStartDelaySeconds;
    public int DefaultDurationSeconds { get; set; } = AuctionRules.DefaultDurationSeconds;
    public decimal DefaultTargetPercentage { get; set; } = AuctionRules.DefaultTargetPercentage;
    public decimal DefaultStartingPercentage { get; set; } = AuctionRules.DefaultStartingPercentage;
    public decimal DefaultFinalPhasePercentage { get; set; } = AuctionRules.DefaultFinalPhasePercentage;
    public decimal DefaultBidIncrement { get; set; } = AuctionRules.DefaultBidIncrement;

    public ExpirationPolicy DefaultExpirationPolicy { get; set; } = ExpirationPolicy.EndAndDistribute;

    /// <summary>Seconds added to the auction when the Extend expiration policy fires.</summary>
    public int ExtensionSeconds { get; set; } = 60;

    public bool RefundLosingParticipantsEntryFee { get; set; } = true;
    public bool RefundWinnerEntryFee { get; set; }
    public WalletType RefundDestinationWallet { get; set; } = WalletType.Shopping;

    public string DefaultCurrency { get; set; } = Money.DefaultCurrency;

    /// <summary>
    /// Hours a winner has to pay for their device, counted from when the auction closes. The terms
    /// of use promise 24; change both together.
    /// </summary>
    public int PrizeClaimPaymentHours { get; set; } = 24;

    /// <summary>Maximum bids a single participant may submit per second.</summary>
    public int BidRateLimitPerSecond { get; set; } = 5;
    public int BidRateLimitPerMinute { get; set; } = 120;

    /// <summary>
    /// Require a one-time code to confirm a withdrawal. On by default: a withdrawal moves real
    /// money out of the platform, so a stolen access token alone must not be enough.
    /// </summary>
    public bool RequireOtpForWithdrawal { get; set; } = true;
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "auction-platform";
    public string Audience { get; set; } = "auction-platform-clients";

    /// <summary>Supplied through environment variables or user secrets. Never committed.</summary>
    public string Secret { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>
    /// Lifetime of a refresh token in days. <b>0 means the session never expires on its own</b>:
    /// it lasts until the user signs out, or until it is revoked by a password change, the account
    /// being disabled, or refresh-token theft detection.
    ///
    /// This is the default because users expect to stay signed in. Rotation still applies on every
    /// refresh, so a stolen token is still single-use and still detected.
    /// </summary>
    public int RefreshTokenDays { get; set; }
}

/// <summary>One-time code policy, shared by the login and withdrawal flows.</summary>
public sealed class OtpOptions
{
    public const string SectionName = "Otp";

    public int CodeLength { get; set; } = 6;

    /// <summary>How long a code stays valid.</summary>
    public int ValidForSeconds { get; set; } = 300;

    /// <summary>Wrong guesses before the code is retired, to stop brute forcing.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Minimum gap between two code requests for the same destination and purpose.</summary>
    public int ResendCooldownSeconds { get; set; } = 60;

    /// <summary>Codes a destination may request per hour, across all purposes.</summary>
    public int MaxRequestsPerHour { get; set; } = 10;

    /// <summary>
    /// Whether an OTP login may create an account that does not exist yet. Off by default: this
    /// platform holds wallets, so account creation stays an explicit, deliberate act.
    /// </summary>
    public bool AllowSignUpOnLogin { get; set; }

    /// <summary>
    /// Echo the code in the API response outside Production so the flow is testable without a
    /// real SMS or email provider. Ignored entirely in Production.
    /// </summary>
    public bool ExposeCodeInDevelopment { get; set; } = true;

    /// <summary>
    /// Phone numbers that sign in with a fixed code instead of an SMS, keyed by number.
    ///
    /// <para>
    /// For store reviewers and for testing: Apple and Google both require a working demo login,
    /// and their reviewers cannot receive a text on a Saudi number. A number listed here is never
    /// sent to the SMS provider — nothing is charged — and accepts only its own code.
    /// </para>
    /// <para>
    /// Empty by default, so it does nothing unless configured, and it is configured only from the
    /// environment (<c>Otp__TestAccounts__0500000001=246810</c>), never from a committed file: the
    /// code is a password. It opens sign-in only. A withdrawal or a prize payment from one of these
    /// numbers still needs a real code, so a known code can never move money.
    /// </para>
    /// </summary>
    public Dictionary<string, string> TestAccounts { get; set; } = new();
}

public sealed class RedisOptions
{
    public const string SectionName = "Redis";
    public string ConnectionString { get; set; } = "localhost:6379";
    public bool Enabled { get; set; } = true;
    public string InstanceName { get; set; } = "auction:";
}

public sealed class FirebaseOptions
{
    public const string SectionName = "Firebase";
    public bool Enabled { get; set; }
    public string? CredentialsJson { get; set; }
    public string? CredentialsPath { get; set; }
    public string? ProjectId { get; set; }
}

public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>Shared secret used to verify inbound webhook signatures.</summary>
    public string WebhookSecret { get; set; } = string.Empty;
    public string Provider { get; set; } = "SANDBOX";
    public bool AllowSandboxDeposits { get; set; } = true;
}

/// <summary>
/// Tawked, the Saudi provider that delivers and checks one-time codes for mobile numbers.
///
/// Tawked issues the code itself; there is no way to hand it one of ours. So for an SMS
/// destination it is not a transport behind our own code — it holds the code, and we hold
/// everything about what that code is allowed to authorise. Email destinations never reach it:
/// it serves Saudi mobile numbers only.
/// </summary>
public sealed class TawkedOptions
{
    public const string SectionName = "Tawked";

    /// <summary>
    /// A key from the application's Keys tab. `tk_test_` is the sandbox — a real key against the
    /// real API that still sends real messages — and `tk_live_` is production.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://tawked.com";

    /// <summary>Partner keys (`tk_partner_`) only: which provisioned application is sending.</summary>
    public string? Application { get; set; }

    /// <summary>
    /// Which autofill lines to put on the message: `web`, `android`, `none`, or empty to send
    /// whatever the Tawked console has configured for the application.
    /// </summary>
    public string? Autofill { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Without a key there is nothing to call, and codes fall back to the local path.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>True for a sandbox key, which is worth saying out loud in the log at startup.</summary>
    public bool IsSandbox => ApiKey.StartsWith("tk_test_", StringComparison.Ordinal);
}
