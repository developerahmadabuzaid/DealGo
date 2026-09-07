using AuctionPlatform.Domain.Entities;

namespace AuctionPlatform.Domain.Rules;

/// <summary>
/// Ranking is decided by the highest price a participant reached, with the earliest server
/// timestamp at which they reached it as the tie-break. Device timestamps are never consulted.
/// </summary>
public static class RankingRules
{
    public const int WinnerCount = 4;

    /// <summary>Orders participants best-first. Participants who never bid rank last.</summary>
    public static IEnumerable<AuctionParticipant> Rank(IEnumerable<AuctionParticipant> participants) =>
        participants
            .OrderByDescending(p => p.HighestPrice)
            .ThenBy(p => p.HighestPriceAt ?? DateTime.MaxValue)
            .ThenBy(p => p.JoinedAt)
            .ThenBy(p => p.Id);

    /// <summary>
    /// Assigns 1-based ranks in place and returns the ordered list. A participant who never
    /// placed a bid still receives a rank, but can never be a winner.
    /// </summary>
    public static List<AuctionParticipant> AssignRanks(IEnumerable<AuctionParticipant> participants)
    {
        var ordered = Rank(participants).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].CurrentRank = i + 1;
        }
        return ordered;
    }

    /// <summary>
    /// The top four rankable participants. Only participants who actually bid are eligible,
    /// so an auction with fewer than four bidders produces fewer than four winners.
    /// </summary>
    public static List<AuctionParticipant> SelectWinners(IEnumerable<AuctionParticipant> participants) =>
        Rank(participants)
            .Where(p => p.BidCount > 0 && p.HighestPrice > 0)
            .Take(WinnerCount)
            .ToList();
}
