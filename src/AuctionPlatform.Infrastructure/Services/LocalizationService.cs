using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Notifications;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// Resolves notification text per user language. Arabic is the platform default; business logic
/// only ever passes keys, so no localized string is embedded in a service.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    public const string DefaultLanguage = "ar";

    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
    {
        ["ar"] = new Dictionary<string, string>
        {
            [NotificationKeys.RequiredParticipantsReachedTitle] = "اكتمل العدد المطلوب",
            [NotificationKeys.RequiredParticipantsReachedBody] = "تم اكتمال {0} مشارك. سيبدأ المزاد قريباً.",
            [NotificationKeys.AuctionStartScheduledTitle] = "تم تحديد موعد بدء المزاد",
            [NotificationKeys.AuctionStartScheduledBody] = "سيبدأ المزاد في الموعد المحدد.",
            [NotificationKeys.AuctionStartTimeChangedTitle] = "تم تغيير موعد بدء المزاد",
            [NotificationKeys.AuctionStartTimeChangedBody] = "تم تحديث موعد بدء المزاد. تحقق من التطبيق.",
            [NotificationKeys.AuctionStartedTitle] = "بدأ المزاد",
            [NotificationKeys.AuctionStartedBody] = "المزاد مباشر الآن. سارع بالمزايدة!",
            [NotificationKeys.FinalPhaseStartedTitle] = "المرحلة النهائية",
            [NotificationKeys.FinalPhaseStartedBody] = "دخل المزاد المرحلة النهائية. الوقت ينفد!",
            [NotificationKeys.AuctionFinishedTitle] = "انتهى المزاد",
            [NotificationKeys.AuctionFinishedBody] = "انتهى المزاد. جارٍ إعلان الفائزين.",
            [NotificationKeys.YouWonTitle] = "مبروك! لقد فزت",
            [NotificationKeys.YouWonBody] = "حصلت على المركز {0} وربحت {1}.",
            [NotificationKeys.YouLostTitle] = "انتهى المزاد",
            [NotificationKeys.YouLostBody] = "لم تكن ضمن الأربعة الأوائل. تمت إعادة رسوم الاشتراك كرصيد تسوق.",
            [NotificationKeys.ShoppingCreditAddedTitle] = "تمت إضافة رصيد تسوق",
            [NotificationKeys.ShoppingCreditAddedBody] = "تمت إضافة {0} إلى محفظة التسوق. يمكن استخدامه في المتجر فقط.",
            [NotificationKeys.PrizeClaimAvailableTitle] = "جائزتك جاهزة للاستلام",
            [NotificationKeys.PrizeClaimAvailableBody] = "أكمل الدفع لاستلام جائزتك.",
            [NotificationKeys.PrizePaymentReminderTitle] = "تذكير بدفع قيمة الجائزة",
            [NotificationKeys.PrizePaymentReminderBody] = "تبقى وقت محدود لدفع قيمة جائزتك.",
            [NotificationKeys.StoreOrderCreatedTitle] = "تم إنشاء الطلب",
            [NotificationKeys.StoreOrderCreatedBody] = "تم استلام طلبك رقم {0}.",
            [NotificationKeys.OrderShippedTitle] = "تم شحن طلبك",
            [NotificationKeys.OrderShippedBody] = "طلبك رقم {0} في الطريق إليك.",
            [NotificationKeys.OrderDeliveredTitle] = "تم تسليم طلبك",
            [NotificationKeys.OrderDeliveredBody] = "تم تسليم طلبك رقم {0}.",
            [NotificationKeys.AuctionCancelledTitle] = "تم إلغاء المزاد",
            [NotificationKeys.AuctionCancelledBody] = "تم إلغاء المزاد وسيتم رد المبالغ المستحقة.",
            [NotificationKeys.LowStockTitle] = "تنبيه: مخزون منخفض",
            [NotificationKeys.LowStockBody] = "المنتج {0} تبقى منه {1} فقط."
        },
        ["en"] = new Dictionary<string, string>
        {
            [NotificationKeys.RequiredParticipantsReachedTitle] = "Participant target reached",
            [NotificationKeys.RequiredParticipantsReachedBody] = "All {0} participants have joined. The auction starts soon.",
            [NotificationKeys.AuctionStartScheduledTitle] = "Auction start scheduled",
            [NotificationKeys.AuctionStartScheduledBody] = "The auction now has a scheduled start time.",
            [NotificationKeys.AuctionStartTimeChangedTitle] = "Auction start time changed",
            [NotificationKeys.AuctionStartTimeChangedBody] = "The start time was updated. Check the app for details.",
            [NotificationKeys.AuctionStartedTitle] = "The auction has started",
            [NotificationKeys.AuctionStartedBody] = "The auction is live. Start bidding now!",
            [NotificationKeys.FinalPhaseStartedTitle] = "Final phase",
            [NotificationKeys.FinalPhaseStartedBody] = "The auction entered its final phase. Time is running out!",
            [NotificationKeys.AuctionFinishedTitle] = "Auction finished",
            [NotificationKeys.AuctionFinishedBody] = "The auction has ended. Winners are being declared.",
            [NotificationKeys.YouWonTitle] = "Congratulations, you won!",
            [NotificationKeys.YouWonBody] = "You finished #{0} and won {1}.",
            [NotificationKeys.YouLostTitle] = "Auction finished",
            [NotificationKeys.YouLostBody] = "You did not finish in the top 4. Your entry fee was returned as shopping credit.",
            [NotificationKeys.ShoppingCreditAddedTitle] = "Shopping credit added",
            [NotificationKeys.ShoppingCreditAddedBody] = "{0} was added to your Shopping Wallet. It can only be spent in the store.",
            [NotificationKeys.PrizeClaimAvailableTitle] = "Your prize is ready to claim",
            [NotificationKeys.PrizeClaimAvailableBody] = "Complete the payment to claim your prize.",
            [NotificationKeys.PrizePaymentReminderTitle] = "Prize payment reminder",
            [NotificationKeys.PrizePaymentReminderBody] = "There is limited time left to pay for your prize.",
            [NotificationKeys.StoreOrderCreatedTitle] = "Order created",
            [NotificationKeys.StoreOrderCreatedBody] = "We received your order {0}.",
            [NotificationKeys.OrderShippedTitle] = "Your order has shipped",
            [NotificationKeys.OrderShippedBody] = "Order {0} is on its way.",
            [NotificationKeys.OrderDeliveredTitle] = "Your order was delivered",
            [NotificationKeys.OrderDeliveredBody] = "Order {0} has been delivered.",
            [NotificationKeys.AuctionCancelledTitle] = "Auction cancelled",
            [NotificationKeys.AuctionCancelledBody] = "The auction was cancelled and any amounts due will be returned.",
            [NotificationKeys.LowStockTitle] = "Low stock alert",
            [NotificationKeys.LowStockBody] = "{0} has only {1} remaining."
        }
    };

    public string Translate(string key, string language, params object[] args)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.ToLowerInvariant();

        if (!Strings.TryGetValue(lang, out var table))
        {
            table = Strings[DefaultLanguage];
        }

        if (!table.TryGetValue(key, out var template))
        {
            // Falling back to the key keeps a missing translation visible rather than silent.
            return Strings[DefaultLanguage].TryGetValue(key, out var fallback) ? Format(fallback, args) : key;
        }

        return Format(template, args);
    }

    private static string Format(string template, object[] args)
    {
        if (args.Length == 0) return template;
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
