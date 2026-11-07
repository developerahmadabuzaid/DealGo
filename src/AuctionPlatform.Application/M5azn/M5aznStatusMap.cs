using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Application.M5azn;

/// <summary>
/// Reads M5azn's status word. Its documentation lists none, so this goes by meaning rather than
/// by an exact list, in English and Arabic, and answers null for a word it does not recognise:
/// the shipment then keeps its status and shows M5azn's word as it came.
/// </summary>
public static class M5aznStatusMap
{
    public static M5aznShipmentStatus? Map(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        var s = remote.Trim().ToLowerInvariant();

        // Order matters: "not delivered" or "returned" must not read as delivered.
        if (Has(s, "not deliver", "undeliver", "لم يتم")) return null;
        if (Has(s, "cancel", "reject", "return", "refund", "failed", "ملغ", "مرفوض", "مرتجع", "إلغاء", "الغاء"))
            return M5aznShipmentStatus.Cancelled;
        // "out for delivery" is still on its way.
        if (Has(s, "out_for", "out for", "transit", "on the way", "with_driver", "في الطريق", "مع المندوب"))
            return M5aznShipmentStatus.Shipped;
        if (Has(s, "deliver", "complete", "تم التوصيل", "تم التسليم", "مكتمل", "مستلم"))
            return M5aznShipmentStatus.Delivered;
        if (Has(s, "ship", "transit", "out_for", "out for", "dispatch", "courier", "with_driver", "شحن", "الطريق", "مندوب"))
            return M5aznShipmentStatus.Shipped;
        if (Has(s, "process", "prepar", "confirm", "accept", "packed", "pending", "new", "received",
                "قيد", "تجهيز", "جديد", "مؤكد"))
            return M5aznShipmentStatus.Processing;

        return null;
    }

    /// <summary>How far along a status is, so a late or repeated answer never moves a shipment backwards.</summary>
    public static int Rank(M5aznShipmentStatus status) => status switch
    {
        M5aznShipmentStatus.Submitted => 1,
        M5aznShipmentStatus.Processing => 2,
        M5aznShipmentStatus.Shipped => 3,
        M5aznShipmentStatus.Delivered => 4,
        _ => 0
    };

    private static bool Has(string s, params string[] words) => words.Any(w => s.Contains(w, StringComparison.Ordinal));
}
