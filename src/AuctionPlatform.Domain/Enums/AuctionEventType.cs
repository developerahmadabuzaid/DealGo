namespace AuctionPlatform.Domain.Enums;

public enum AuctionEventType
{
    ParticipantJoined = 0,
    RequiredParticipantsReached = 1,
    AuctionStartScheduled = 2,
    AuctionStartTimeChanged = 3,
    AuctionStartedManually = 4,
    AuctionStarted = 5,
    FinalPhaseStarted = 6,
    BidPlaced = 7,
    TargetReached = 8,
    AuctionFinished = 9,
    WinnersDeclared = 10,
    AuctionPaused = 11,
    AuctionResumed = 12,
    AuctionCancelled = 13,
    AuctionExpired = 14,
    EntryFeeRefunded = 15,
    ShoppingCreditAdded = 16,
    AuctionCreated = 17,
    AuctionPublished = 18,
    AuctionUpdated = 19
}
