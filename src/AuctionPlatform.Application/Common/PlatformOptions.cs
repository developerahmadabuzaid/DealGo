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

    /// <summary>
    /// Turns on the generic /webhooks/payments/{provider} endpoint, which credits the user and
    /// amount its payload names. No real provider uses it (Paymob has its own endpoint), so it is off
    /// unless set, never served in Production, and refused with a secret under 32 characters:
    /// production ran it with the literal secret "not-configured", which let anyone credit any
    /// wallet.
    /// </summary>
    public bool GenericWebhookEnabled { get; set; }
    public string Provider { get; set; } = "SANDBOX";

    /// <summary>
    /// Lets POST /wallet/deposit add balance with no payment behind it, for development. Never
    /// honoured in Production, whatever this says: there it was a way to mint money.
    /// </summary>
    public bool AllowSandboxDeposits { get; set; } = true;
}

/// <summary>
/// Paymob, the Saudi gateway that takes card, mada and Apple Pay payments for wallet top-ups.
/// Follows Paymob's developer documentation (developers.paymob.com, Intention APIs, Unified
/// Checkout, Webhook callbacks and HMAC, Transaction inquiry).
///
/// From the Paymob dashboard: the secret key creates payments ("Token" header), the public key
/// opens the checkout page, the HMAC secret proves a callback came from Paymob, and the API key
/// lets the server ask Paymob about a transaction itself. Each payment method enabled for the
/// account has its own integration id (Developers → Payment integrations).
/// </summary>
public sealed class PaymobOptions
{
    public const string SectionName = "Paymob";

    /// <summary>Paymob's KSA API host: intentions, auth tokens and transaction inquiry.</summary>
    public string BaseUrl { get; set; } = "https://ksa.paymob.com";

    /// <summary>Paymob's KSA Unified Checkout page, opened with the public key and client secret.</summary>
    public string CheckoutUrl { get; set; } = "https://ksa.checkout.paymob.com";

    public string SecretKey { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string HmacSecret { get; set; } = string.Empty;
    public int[] IntegrationIds { get; set; } = [];

    /// <summary>
    /// The API key, used only to ask Paymob about a transaction (reconciliation, and checking a
    /// customer's return). Optional: without it the processed callback is the only confirmation.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// "test" or "live", stated on purpose. Paymob marks every payment method it opens as live or
    /// not; a payment whose methods disagree with this is refused, so test keys can never take
    /// payments on a site meant to be live, nor live keys on one meant for testing.
    /// </summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>
    /// The site's public address, which Paymob calls back and returns the customer to. Empty uses
    /// the address the top-up request arrived on, which is right everywhere but behind a proxy.
    /// </summary>
    public string CallbackBaseUrl { get; set; } = string.Empty;

    /// <summary>The smallest and largest single top-up, in riyals.</summary>
    public decimal MinTopUp { get; set; } = 10m;
    public decimal MaxTopUp { get; set; } = 20000m;

    /// <summary>How long a customer has to pay once a top-up is opened, in seconds.</summary>
    public int IntentionExpirySeconds { get; set; } = 3600;

    /// <summary>How many unpaid top-ups one user may have open at once.</summary>
    public int MaxOpenTopUps { get; set; } = 5;

    public int TimeoutSeconds { get; set; } = 20;

    public bool IsLiveMode => string.Equals(Mode, "live", StringComparison.OrdinalIgnoreCase);
    public bool IsTestMode => string.Equals(Mode, "test", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SecretKey) &&
        !string.IsNullOrWhiteSpace(PublicKey) &&
        !string.IsNullOrWhiteSpace(HmacSecret) &&
        IntegrationIds.Length > 0 &&
        (IsLiveMode || IsTestMode);

    public bool CanInquire => IsConfigured && !string.IsNullOrWhiteSpace(ApiKey);
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
