namespace AuctionPlatform.Domain.Enums;

public enum ParticipantStatus
{
    PendingPayment = 0,
    Active = 1,
    Won = 2,
    Lost = 3,
    Refunded = 4,
    Disqualified = 5,
    Withdrawn = 6
}
