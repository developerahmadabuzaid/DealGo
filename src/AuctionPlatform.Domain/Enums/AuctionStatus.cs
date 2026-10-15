namespace AuctionPlatform.Domain.Enums;

/// <summary>
/// Auction lifecycle states. Transitions are validated exclusively by the backend
/// (see <see cref="Rules.AuctionStateMachine"/>). Clients may never request a state directly.
/// </summary>
public enum AuctionStatus
{
    // An admin is still writing it. Nobody can see it.
    Draft = 0,

    // Written and approved, but registration hasn't opened yet.
    Scheduled = 1,

    // People can join now and pay the entry fee.
    RegistrationOpen = 2,

    //Still open to join, but it's getting full — this is the "almost there" state.
    WaitingForParticipants = 3,

    //Enough people joined. It can start any second.
    Ready = 4,

    //Enough people joined, but there's a countdown before it begins.
    WaitingForStart = 5,

    //"Ready" and "WaitingForStart" are the same situation with one difference:
    // whether the auction was set up with a waiting delay.
    // No delay → Ready. Delay → WaitingForStart.


    //Bidding is open. The price is climbing.
    Live = 6,

    //	Still bidding, but the price is close to the target. This is the "hurry up" state — the app shows the timer and turns urgent
    FinalPhase = 7,

    //Bidding is over. Nobody knows who won yet.
    Finished = 8,

    //The top 4 have been worked out, and everyone else got their entry fee back as shopping credit.
    WinnersDeclared = 9,

    //""Finished" and "WinnersDeclared" are separate on purpose.
    //There's a real gap between "bidding stopped" and "we've decided who won" —
    //the server has to lock the records, rank everyone fairly, and pay out the refunds.
    //If those were one status, a crash halfway through would leave the auction claiming to have winners when it didn't.

    //The winners are now collecting their prizes.
    PrizeClaim = 10,

    //Everything is done. Prizes handed over, money settled. Nothing more will happen.
    Completed = 11,

    //An admin hit pause. It can be resumed.
    Paused = 12,

    //Called off. It's over for good and can't come back.
    Cancelled = 13,

    //Time ran out before anything happened — not enough people joined, or nobody reached the target.
    Expired = 14
}
