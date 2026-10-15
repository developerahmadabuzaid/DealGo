using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;

namespace AuctionPlatform.Domain.Rules;

/// <summary>
/// Pricing and publish-time validation rules. Target and final-phase prices are computed once
/// from the original price and then persisted, so editing the catalog later cannot retroactively
/// change a running or historical auction.
/// </summary>
public static class AuctionRules
{
    public const decimal DefaultEntryFee = 20m;
    public const int DefaultRequiredParticipants = 400;
    public const int DefaultDurationSeconds = 180;          // 3 minutes
    public const decimal DefaultTargetPercentage = 50m;
    public const decimal DefaultFinalPhasePercentage = 40m;
    public const decimal DefaultBidIncrement = 20m;
    public const int DefaultStartDelaySeconds = 7200;       // 2 hours

    public static decimal CalculateTargetPrice(decimal originalPrice, decimal targetPercentage) =>
        Money.Round(originalPrice * targetPercentage / 100m);

    public static decimal CalculateFinalPhasePrice(decimal originalPrice, decimal finalPhasePercentage) =>
        Money.Round(originalPrice * finalPhasePercentage / 100m);

    /// <summary>
    /// Validates everything that must hold before an auction may be published. Returns the list of
    /// violations rather than throwing so the admin UI can show them all at once.
    /// </summary>
    public static IReadOnlyList<string> ValidateForPublish(Auction auction, int prizeCount, IEnumerable<int> prizeRanks)
    {
        var errors = new List<string>();

        if (auction.OriginalPrice <= 0)
            errors.Add("OriginalPrice must be greater than 0.");

        if (auction.StartingPrice <= 0)
            errors.Add("StartingPrice must be greater than 0.");

        if (auction.BidIncrement <= 0)
            errors.Add("BidIncrement must be greater than 0.");

        if (auction.EntryFee < 0)
            errors.Add("EntryFee cannot be negative.");

        if (auction.RequiredParticipants < 1)
            errors.Add("RequiredParticipants must be at least 1.");

        if (auction.DurationSeconds <= 0)
            errors.Add("DurationSeconds must be greater than 0.");

        if (auction.StartDelayAfterParticipantsSeconds < 0)
            errors.Add("StartDelayAfterParticipantsSeconds cannot be negative.");

        if (auction.TargetPercentage is <= 0 or > 100)
            errors.Add("TargetPercentage must be between 0 (exclusive) and 100.");

        if (auction.FinalPhasePercentage is <= 0 or > 100)
            errors.Add("FinalPhasePercentage must be between 0 (exclusive) and 100.");

        if (auction.StartingPrice >= auction.TargetPrice)
            errors.Add("StartingPrice must be less than TargetPrice.");

        if (auction.TargetPrice >= auction.OriginalPrice)
            errors.Add("TargetPrice must be less than OriginalPrice.");

        if (auction.FinalPhasePrice >= auction.TargetPrice)
            errors.Add("FinalPhasePrice must be less than TargetPrice.");

        if (prizeCount != 4)
            errors.Add("An auction must have exactly 4 prizes.");

        var ranks = prizeRanks.OrderBy(r => r).ToArray();
        if (!ranks.SequenceEqual(new[] { 1, 2, 3, 4 }))
            errors.Add("Prize ranks must be exactly 1, 2, 3 and 4, each used once.");

        if (string.IsNullOrWhiteSpace(auction.Title))
            errors.Add("Title is required.");

        if (string.IsNullOrWhiteSpace(auction.MainProductNameSnapshot))
            errors.Add("The rank 1 prize must have a name.");

        return errors;
    }

    public static void EnsurePublishable(Auction auction, int prizeCount, IEnumerable<int> prizeRanks)
    {
        var errors = ValidateForPublish(auction, prizeCount, prizeRanks);
        if (errors.Count > 0)
        {
            throw new DomainException(
                ErrorCodes.InvalidAuctionConfiguration,
                "The auction cannot be published because its configuration is invalid.",
                errors);
        }
    }

    /// <summary>
    /// The price a bid would produce. The increment moves the auction price; it is not a charge
    /// against the bidder, whose only payment is the separate entry fee.
    /// </summary>
    public static decimal NextPrice(decimal currentPrice, decimal bidIncrement, decimal targetPrice)
    {
        var next = Money.Round(currentPrice + bidIncrement);
        return next > targetPrice ? targetPrice : next;
    }

    public static bool ReachesTarget(decimal newPrice, decimal targetPrice) => newPrice >= targetPrice;

    public static bool EntersFinalPhase(decimal newPrice, decimal finalPhasePrice) =>
        newPrice >= finalPhasePrice;

    /// <summary>
    /// What a winner pays for a prize: the prize's own price with the auction's discount applied.
    ///
    /// The discount is set by where the auction price finished against the auction's value, and
    /// the same fraction applies to every prize, not only the first. An auction that closed at
    /// 2500 on a 5000 value is a 50% discount, so a 1200 fourth prize costs its winner 600.
    /// A missing or non-positive value leaves the prize at full price rather than dividing by it.
    /// </summary>
    public static decimal PrizePaymentAmount(decimal prizePrice, decimal finalPrice, decimal auctionValue)
    {
        if (auctionValue <= 0 || finalPrice <= 0) return Money.Round(prizePrice);

        var fraction = finalPrice / auctionValue;
        if (fraction > 1m) fraction = 1m;

        return Money.Round(prizePrice * fraction);
    }

    /// <summary>Progress from the starting price toward the target, clamped to 0..1.</summary>
    public static decimal Progress(decimal startingPrice, decimal currentPrice, decimal targetPrice)
    {
        var span = targetPrice - startingPrice;
        if (span <= 0) return 1m;
        var progress = (currentPrice - startingPrice) / span;
        return progress < 0 ? 0m : progress > 1m ? 1m : progress;
    }
}
