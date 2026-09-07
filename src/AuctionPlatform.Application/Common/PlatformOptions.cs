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
    public decimal DefaultFinalPhasePercentage { get; set; } = AuctionRules.DefaultFinalPhasePercentage;
    public decimal DefaultBidIncrement { get; set; } = AuctionRules.DefaultBidIncrement;

    public ExpirationPolicy DefaultExpirationPolicy { get; set; } = ExpirationPolicy.EndAndDistribute;

    /// <summary>Seconds added to the auction when the Extend expiration policy fires.</summary>
    public int ExtensionSeconds { get; set; } = 60;

    public bool RefundLosingParticipantsEntryFee { get; set; } = true;
    public bool RefundWinnerEntryFee { get; set; }
    public WalletType RefundDestinationWallet { get; set; } = WalletType.Shopping;

    public string DefaultCurrency { get; set; } = Money.DefaultCurrency;

    /// <summary>Days a winner has to pay for their prize before the claim expires.</summary>
    public int PrizeClaimPaymentDays { get; set; } = 7;

    /// <summary>Maximum bids a single participant may submit per second.</summary>
    public int BidRateLimitPerSecond { get; set; } = 5;
    public int BidRateLimitPerMinute { get; set; } = 120;
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "auction-platform";
    public string Audience { get; set; } = "auction-platform-clients";

    /// <summary>Supplied through environment variables or user secrets. Never committed.</summary>
    public string Secret { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
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
