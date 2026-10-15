using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Domain.Rules;

/// <summary>
/// The only place auction state transitions are authorised. Clients never name a target state;
/// they invoke an operation and the backend decides whether the resulting transition is legal.
/// </summary>
public static class AuctionStateMachine
{
    private static readonly Dictionary<AuctionStatus, AuctionStatus[]> Allowed = new()
    {
        [AuctionStatus.Draft] = new[]
        {
            AuctionStatus.Scheduled, AuctionStatus.RegistrationOpen, AuctionStatus.Cancelled
        },
        [AuctionStatus.Scheduled] = new[]
        {
            AuctionStatus.RegistrationOpen, AuctionStatus.Cancelled
        },
        [AuctionStatus.RegistrationOpen] = new[]
        {
            AuctionStatus.WaitingForParticipants, AuctionStatus.Ready,
            AuctionStatus.Cancelled, AuctionStatus.Expired
        },
        [AuctionStatus.WaitingForParticipants] = new[]
        {
            AuctionStatus.Ready, AuctionStatus.Cancelled, AuctionStatus.Expired
        },
        [AuctionStatus.Ready] = new[]
        {
            AuctionStatus.WaitingForStart, AuctionStatus.Live, AuctionStatus.Cancelled
        },
        [AuctionStatus.WaitingForStart] = new[]
        {
            AuctionStatus.Live, AuctionStatus.Cancelled, AuctionStatus.Paused
        },
        [AuctionStatus.Live] = new[]
        {
            AuctionStatus.FinalPhase, AuctionStatus.Finished,
            AuctionStatus.Paused, AuctionStatus.Cancelled, AuctionStatus.Expired
        },
        [AuctionStatus.FinalPhase] = new[]
        {
            AuctionStatus.Finished, AuctionStatus.Paused,
            AuctionStatus.Cancelled, AuctionStatus.Expired
        },
        [AuctionStatus.Paused] = new[]
        {
            AuctionStatus.Live, AuctionStatus.FinalPhase,
            AuctionStatus.WaitingForStart, AuctionStatus.Cancelled
        },
        [AuctionStatus.Finished] = new[]
        {
            AuctionStatus.WinnersDeclared, AuctionStatus.Cancelled
        },
        [AuctionStatus.WinnersDeclared] = new[]
        {
            AuctionStatus.PrizeClaim, AuctionStatus.Completed
        },
        [AuctionStatus.PrizeClaim] = new[]
        {
            AuctionStatus.Completed
        },
        [AuctionStatus.Expired] = new[]
        {
            AuctionStatus.Finished, AuctionStatus.Cancelled, AuctionStatus.RegistrationOpen
        },
        [AuctionStatus.Completed] = Array.Empty<AuctionStatus>(),
        [AuctionStatus.Cancelled] = Array.Empty<AuctionStatus>()
    };

    public static bool CanTransition(AuctionStatus from, AuctionStatus to) =>
        from == to || (Allowed.TryGetValue(from, out var targets) && targets.Contains(to));

    /// <summary>Throws when the transition is not permitted.</summary>
    public static void EnsureCanTransition(AuctionStatus from, AuctionStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new DomainException(
                ErrorCodes.InvalidAuctionState,
                $"Cannot transition auction from {from} to {to}.");
        }
    }

    public static IReadOnlyList<AuctionStatus> AllowedTransitions(AuctionStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : Array.Empty<AuctionStatus>();

    /// <summary>States in which a user may still register and pay the entry fee.</summary>
    public static bool AcceptsRegistration(AuctionStatus status) =>
        status == AuctionStatus.RegistrationOpen ||
        status == AuctionStatus.WaitingForParticipants;

    /// <summary>States in which the bid endpoint may accept work.</summary>
    public static bool AcceptsBids(AuctionStatus status) =>
        status == AuctionStatus.Live || status == AuctionStatus.FinalPhase;

    public static bool IsTerminal(AuctionStatus status) =>
        status == AuctionStatus.Completed || status == AuctionStatus.Cancelled;

    /// <summary>
    /// States an auction may be shown in the front page's featured row.
    ///
    /// The row is a place to act, not a history: every card there offers to take a seat or to
    /// bid. An auction a visitor can do neither with — still a draft, paused, finished, cancelled
    /// — would be an advertisement for a door that does not open, so it is refused when an
    /// administrator picks it and dropped from the row the moment it leaves these states.
    /// </summary>
    public static bool CanBeFeatured(AuctionStatus status) =>
        AcceptsRegistration(status) ||
        AcceptsBids(status) ||
        status == AuctionStatus.Ready ||
        status == AuctionStatus.WaitingForStart;
}
