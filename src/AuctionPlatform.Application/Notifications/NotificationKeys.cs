namespace AuctionPlatform.Application.Notifications;

/// <summary>
/// Localization keys for user-facing notification text. Business logic references keys only;
/// no Arabic or English string is ever hardcoded into a service.
/// </summary>
public static class NotificationKeys
{
    public const string RequiredParticipantsReachedTitle = "notification.participants_reached.title";
    public const string RequiredParticipantsReachedBody = "notification.participants_reached.body";

    public const string AuctionStartScheduledTitle = "notification.start_scheduled.title";
    public const string AuctionStartScheduledBody = "notification.start_scheduled.body";

    public const string AuctionStartTimeChangedTitle = "notification.start_time_changed.title";
    public const string AuctionStartTimeChangedBody = "notification.start_time_changed.body";

    public const string AuctionStartedTitle = "notification.auction_started.title";
    public const string AuctionStartedBody = "notification.auction_started.body";

    public const string FinalPhaseStartedTitle = "notification.final_phase.title";
    public const string FinalPhaseStartedBody = "notification.final_phase.body";

    public const string AuctionFinishedTitle = "notification.auction_finished.title";
    public const string AuctionFinishedBody = "notification.auction_finished.body";

    public const string YouWonTitle = "notification.you_won.title";
    public const string YouWonBody = "notification.you_won.body";

    public const string YouLostTitle = "notification.you_lost.title";
    public const string YouLostBody = "notification.you_lost.body";

    public const string ShoppingCreditAddedTitle = "notification.shopping_credit_added.title";
    public const string ShoppingCreditAddedBody = "notification.shopping_credit_added.body";

    public const string PrizeClaimAvailableTitle = "notification.prize_claim.title";
    public const string PrizeClaimAvailableBody = "notification.prize_claim.body";

    public const string PrizePaymentReminderTitle = "notification.prize_payment_reminder.title";
    public const string PrizePaymentReminderBody = "notification.prize_payment_reminder.body";

    public const string StoreOrderCreatedTitle = "notification.order_created.title";
    public const string StoreOrderCreatedBody = "notification.order_created.body";

    public const string OrderShippedTitle = "notification.order_shipped.title";
    public const string OrderShippedBody = "notification.order_shipped.body";

    public const string OrderDeliveredTitle = "notification.order_delivered.title";
    public const string OrderDeliveredBody = "notification.order_delivered.body";

    public const string AuctionCancelledTitle = "notification.auction_cancelled.title";
    public const string AuctionCancelledBody = "notification.auction_cancelled.body";

    public const string EntryFeeRefundedTitle = "notification.entry_fee_refunded.title";
    public const string EntryFeeRefundedBody = "notification.entry_fee_refunded.body";

    public const string LowStockTitle = "notification.low_stock.title";
    public const string LowStockBody = "notification.low_stock.body";
}
