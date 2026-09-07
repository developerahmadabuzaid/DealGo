namespace AuctionPlatform.Domain.Enums;

/// <summary>
/// Auction lifecycle states. Transitions are validated exclusively by the backend
/// (see <see cref="Rules.AuctionStateMachine"/>). Clients may never request a state directly.
/// </summary>
public enum AuctionStatus
{
    Draft = 0,
    Scheduled = 1,
    RegistrationOpen = 2,
    WaitingForParticipants = 3,
    Ready = 4,
    WaitingForStart = 5,
    Live = 6,
    FinalPhase = 7,
    Finished = 8,
    WinnersDeclared = 9,
    PrizeClaim = 10,
    Completed = 11,
    Paused = 12,
    Cancelled = 13,
    Expired = 14
}
