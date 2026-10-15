using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Domain.Entities;

/// <summary>
/// A single auction round. All monetary values are persisted rather than derived, so historical
/// auctions never change when the underlying product is later edited. All timestamps are UTC.
/// </summary>
public class Auction : AuditableEntity
{
    /// <summary>Human friendly identifier, e.g. AUC-2026-000001.</summary>
    public string AuctionNumber { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

/// <summary>
    /// Optional link to a shared catalog product, kept for auctions created before prizes moved
    /// onto the auction. The name and image shown to users come from the snapshots below.
    /// </summary>
    public Guid? MainProductId { get; set; }
    public Product? MainProduct { get; set; }

    /// <summary>Snapshot of the main product's name at publish time.</summary>
    public string MainProductNameSnapshot { get; set; } = string.Empty;

    /// <summary>The auction's picture. Taken from the rank 1 prize whenever prizes are saved.</summary>
    public string? MainImageSnapshot { get; set; }

    /// <summary>
    /// The auction's own picture, shown on its card and at the top of its page. Separate from the
    /// prize pictures, so the auction can be advertised with a banner of its own. When empty, the
    /// first prize's picture stands in for it.
    /// </summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>
    /// Picked out by an administrator for the front page. It changes nothing about how the auction
    /// runs; it only decides whether the app shows it in the featured row.
    /// </summary>
    public bool IsFeatured { get; set; }

    /// <summary>
    /// Where this one sits in the featured row, smallest first.
    ///
    /// Only meaningful while <see cref="IsFeatured"/> is true. Ties — and everything an
    /// administrator has never dragged — fall back to newest first, so an auction that has just
    /// been featured appears at the front rather than silently behind everything at zero.
    /// </summary>
    public int FeaturedOrder { get; set; }

    public decimal OriginalPrice { get; set; }
    public decimal StartingPrice { get; set; }
    public decimal CurrentPrice { get; set; }

    public decimal TargetPercentage { get; set; }
    public decimal TargetPrice { get; set; }

    public decimal FinalPhasePercentage { get; set; }
    public decimal FinalPhasePrice { get; set; }

    public decimal BidIncrement { get; set; }

    public decimal EntryFee { get; set; }
    public string Currency { get; set; } = Money.DefaultCurrency;

    public int RequiredParticipants { get; set; }
    public int ParticipantCount { get; set; }

    public int StartDelayAfterParticipantsSeconds { get; set; }
    public int DurationSeconds { get; set; }

    //Times of auction

    //The moment the required participant count was reached
    public DateTime? ParticipantsCompletedAt { get; set; }

    //When the auction is supposed to go live — ParticipantsCompletedAt + StartDelayAfterParticipantsSeconds.
    public DateTime? ScheduledStartAt { get; set; }

    //When it really went live.
    public DateTime? ActualStartedAt { get; set; }
    public DateTime? FinalPhaseStartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public AuctionStatus Status { get; set; } = AuctionStatus.Draft;
    public ExpirationPolicy ExpirationPolicy { get; set; } = ExpirationPolicy.EndAndDistribute;

    public bool TargetReached { get; set; }

    /// <summary>Monotonic bid counter, assigned inside the bid transaction.</summary>
    public long LastSequenceNumber { get; set; }

    // Refund configuration (per-auction override of the platform defaults).
    public bool RefundLosingParticipantsEntryFee { get; set; } = true;
    public bool RefundWinnerEntryFee { get; set; }
    public WalletType RefundDestinationWallet { get; set; } = WalletType.Shopping;

    public bool WinnersDeclared { get; set; }
    public bool RefundsProcessed { get; set; }

    /// <summary>Optimistic concurrency token mapped to the PostgreSQL xmin system column.</summary>
    public uint Version { get; set; }

    public ICollection<AuctionPrize> Prizes { get; set; } = new List<AuctionPrize>();
    public ICollection<AuctionParticipant> Participants { get; set; } = new List<AuctionParticipant>();
    public ICollection<Bid> Bids { get; set; } = new List<Bid>();
    public ICollection<Winner> Winners { get; set; } = new List<Winner>();

    /// <summary>The auction accepts bids only in these states.</summary>
    public bool IsBiddable => Status == AuctionStatus.Live || Status == AuctionStatus.FinalPhase;

    public DateTime? EndsAt =>
        ActualStartedAt.HasValue ? ActualStartedAt.Value.AddSeconds(DurationSeconds) : null;

    public int? RemainingSeconds
    {
        get
        {
            if (EndsAt is null || !IsBiddable) return null;
            var remaining = (int)Math.Ceiling((EndsAt.Value - DateTime.UtcNow).TotalSeconds);
            return remaining < 0 ? 0 : remaining;
        }
    }
}
