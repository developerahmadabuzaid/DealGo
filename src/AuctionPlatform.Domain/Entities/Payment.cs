using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Domain.Entities;

/// <summary>
/// A money movement against an external provider or an internal wallet.
/// UNIQUE(Provider, ProviderTransactionId) keeps repeated webhooks from double-crediting.
/// </summary>
public class Payment : Entity
{
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public Guid? AuctionId { get; set; }
    public Guid? StoreOrderId { get; set; }
    public Guid? PrizeClaimId { get; set; }

    public PaymentType PaymentType { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = Money.DefaultCurrency;

    /// <summary>"WALLET" for internal transfers, otherwise the external gateway name.</summary>
    public string Provider { get; set; } = "WALLET";
    public string? ProviderTransactionId { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public string? IdempotencyKey { get; set; }
    public string? FailureReason { get; set; }
    public string? Metadata { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Raw record of an inbound provider webhook. Persisted before processing so redelivery is
/// detectable and replays never create a second financial transaction.
/// </summary>
public class PaymentWebhookEvent : Entity
{
    public string Provider { get; set; } = string.Empty;
    public string ProviderEventId { get; set; } = string.Empty;
    public string? ProviderTransactionId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string? Signature { get; set; }
    public bool SignatureValid { get; set; }
    public bool Processed { get; set; }
    public string? ProcessingError { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}

/// <summary>
/// Records the conversion of a losing participant's entry fee into wallet credit.
/// UNIQUE(AuctionId, ParticipantId) is what makes refunds exactly-once.
/// </summary>
public class AuctionEntryFeeRefund : Entity
{
    public Guid AuctionId { get; set; }
    public Auction? Auction { get; set; }

    public Guid ParticipantId { get; set; }
    public AuctionParticipant? Participant { get; set; }

    public Guid UserId { get; set; }

    public decimal OriginalEntryFee { get; set; }
    public decimal RefundAmount { get; set; }
    public string Currency { get; set; } = Money.DefaultCurrency;

    public WalletType DestinationWallet { get; set; } = WalletType.Shopping;
    public RefundStatus Status { get; set; } = RefundStatus.Pending;

    public Guid? WalletTransactionId { get; set; }
    public string? FailureReason { get; set; }
    public int AttemptCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}
