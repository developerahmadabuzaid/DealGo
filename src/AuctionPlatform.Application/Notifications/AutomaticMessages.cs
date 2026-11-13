using System.Text.RegularExpressions;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Notifications;

/// <summary>A word staff can put in a message, filled in with each recipient's own value.</summary>
public sealed record MessagePlaceholder(string Token, string Label, string Example);

/// <summary>
/// One message the system sends by itself. <see cref="Placeholders"/> are in the order the code
/// passes its values, so the n-th one is <c>{n}</c> in the built-in text.
/// </summary>
public sealed record AutomaticMessageDefinition(
    string Code,
    string Group,
    string Name,
    string When,
    string Recipients,
    string TitleKey,
    string BodyKey,
    IReadOnlyList<MessagePlaceholder> Placeholders);

/// <summary>
/// Every message the system sends on its own, as staff see them on the Automatic messages page.
/// Messages staff write themselves, and alerts meant for staff, are not here.
/// </summary>
public static class AutomaticMessages
{
    private static readonly MessagePlaceholder Auction = new("auction", "اسم المزاد", "آيفون 17 برو");
    private static readonly MessagePlaceholder Count = new("count", "عدد المشاركين", "10");
    private static readonly MessagePlaceholder Rank = new("rank", "المركز", "1");
    private static readonly MessagePlaceholder Prize = new("prize", "الجائزة", "آيفون 17 برو");
    private static readonly MessagePlaceholder Amount = new("amount", "المبلغ", "25.00 SAR");
    private static readonly MessagePlaceholder Order = new("order", "رقم الطلب", "ORD-2026-000123");

    public static IReadOnlyList<AutomaticMessageDefinition> All { get; } =
    [
        new("new_auction", "المزادات", "مزاد جديد",
            "عند نشر مزاد جديد من لوحة التحكم.", "جميع المستخدمين",
            NotificationKeys.NewAuctionTitle, NotificationKeys.NewAuctionBody, [Auction]),
        new("participants_reached", "المزادات", "اكتمال عدد المشاركين",
            "عند اكتمال العدد المطلوب وتحديد موعد البدء.", "المشتركون في المزاد",
            NotificationKeys.RequiredParticipantsReachedTitle, NotificationKeys.RequiredParticipantsReachedBody, [Count]),
        new("start_time_changed", "المزادات", "تغيير موعد البدء",
            "عند تغيير موعد بدء المزاد من لوحة التحكم.", "المشتركون في المزاد",
            NotificationKeys.AuctionStartTimeChangedTitle, NotificationKeys.AuctionStartTimeChangedBody, []),
        new("auction_started", "المزادات", "بدء المزاد",
            "عند بدء المزاد، في موعده أو بالبدء الفوري.", "المشتركون في المزاد",
            NotificationKeys.AuctionStartedTitle, NotificationKeys.AuctionStartedBody, [Auction]),
        new("you_won", "نتائج المزاد", "الفوز بجائزة",
            "عند إعلان الفائزين، لكل فائز.", "الفائزون",
            NotificationKeys.YouWonTitle, NotificationKeys.YouWonBody, [Rank, Prize]),
        new("you_lost", "نتائج المزاد", "عدم الفوز",
            "عند إعلان الفائزين، لمن لم يكن ضمن الأربعة الأوائل.", "غير الفائزين",
            NotificationKeys.YouLostTitle, NotificationKeys.YouLostBody, []),
        new("prize_claim_expired", "نتائج المزاد", "انتهاء مهلة دفع الجهاز",
            "بعد 24 ساعة من الفوز دون دفع قيمة الجهاز، مع إعادة رسوم الاشتراك لمحفظة التسوق.", "الفائز",
            NotificationKeys.PrizeClaimExpiredTitle, NotificationKeys.PrizeClaimExpiredBody, [Amount, Auction]),
        new("shopping_credit_added", "المحفظة", "إضافة رصيد تسوق",
            "عند رد رسوم الاشتراك كرصيد تسوق بعد المزاد.", "غير الفائزين",
            NotificationKeys.ShoppingCreditAddedTitle, NotificationKeys.ShoppingCreditAddedBody, [Amount]),
        new("entry_fee_refunded", "المحفظة", "رد رسوم الاشتراك",
            "عند رد رسوم الاشتراك إلى المحفظة الرئيسية.", "المشترك",
            NotificationKeys.EntryFeeRefundedTitle, NotificationKeys.EntryFeeRefundedBody, [Amount]),
        new("order_created", "المتجر", "استلام الطلب",
            "عند إتمام طلب من المتجر.", "صاحب الطلب",
            NotificationKeys.StoreOrderCreatedTitle, NotificationKeys.StoreOrderCreatedBody, [Order]),
        new("order_shipped", "المتجر", "شحن الطلب",
            "عند تغيير حالة الطلب إلى «تم الشحن».", "صاحب الطلب",
            NotificationKeys.OrderShippedTitle, NotificationKeys.OrderShippedBody, [Order]),
        new("order_delivered", "المتجر", "تسليم الطلب",
            "عند تغيير حالة الطلب إلى «تم التسليم».", "صاحب الطلب",
            NotificationKeys.OrderDeliveredTitle, NotificationKeys.OrderDeliveredBody, [Order]),
    ];

    private static readonly Dictionary<string, AutomaticMessageDefinition> ByTitleKey =
        All.ToDictionary(d => d.TitleKey, StringComparer.Ordinal);

    /// <summary>The automatic message a notification belongs to; null for one staff wrote.</summary>
    public static AutomaticMessageDefinition? ForTitleKey(string titleKey) =>
        ByTitleKey.GetValueOrDefault(titleKey);

    public static AutomaticMessageDefinition? ForCode(string code) =>
        All.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.Ordinal));

    private static readonly Regex Token = new(@"\{([^{}]*)\}", RegexOptions.Compiled);

    /// <summary>The built-in text with <c>{0}</c> written as the word staff see, <c>{auction}</c>.</summary>
    public static string ToNamed(string text, AutomaticMessageDefinition definition) =>
        Token.Replace(text, m =>
            int.TryParse(m.Groups[1].Value, out var i) && i >= 0 && i < definition.Placeholders.Count
                ? "{" + definition.Placeholders[i].Token + "}"
                : m.Value);

    /// <summary>Staff's text with every placeholder filled in from the values the code passed.</summary>
    public static string Fill(string text, AutomaticMessageDefinition definition, IReadOnlyList<object> values) =>
        Token.Replace(text, m =>
        {
            for (var i = 0; i < definition.Placeholders.Count; i++)
            {
                if (definition.Placeholders[i].Token == m.Groups[1].Value)
                {
                    return i < values.Count ? Convert.ToString(values[i]) ?? string.Empty : string.Empty;
                }
            }
            return m.Value;
        });

    /// <summary>Placeholders a text names that this message cannot fill.</summary>
    public static IReadOnlyList<string> UnknownPlaceholders(string text, AutomaticMessageDefinition definition) =>
        Token.Matches(text)
            .Select(m => m.Groups[1].Value)
            .Where(t => definition.Placeholders.All(p => p.Token != t))
            .Distinct()
            .ToList();
}

/// <summary>One automatic message as the dashboard shows it: the built-in wording and staff's own.</summary>
public sealed record AutomaticMessageDto(
    string Code,
    string Group,
    string Name,
    string When,
    string Recipients,
    IReadOnlyList<MessagePlaceholder> Placeholders,
    bool Enabled,
    AutomaticMessageTextDto Arabic,
    AutomaticMessageTextDto English,
    bool Customized,
    DateTime? UpdatedAt);

/// <summary><c>Title</c>/<c>Body</c>: staff's wording, null when the built-in one is used.</summary>
public sealed record AutomaticMessageTextDto(string? Title, string? Body, string DefaultTitle, string DefaultBody);

/// <summary>Empty or null text: the built-in wording.</summary>
public sealed record UpdateAutomaticMessageRequest(
    bool Enabled, string? TitleAr, string? BodyAr, string? TitleEn, string? BodyEn);

public interface IAutomaticMessageService
{
    Task<IReadOnlyList<AutomaticMessageDto>> ListAsync(CancellationToken ct = default);

    Task<AutomaticMessageDto> UpdateAsync(string code, UpdateAutomaticMessageRequest request, CancellationToken ct = default);

    /// <summary>Back to the built-in wording, switched on.</summary>
    Task<AutomaticMessageDto> ResetAsync(string code, CancellationToken ct = default);
}

public sealed class AutomaticMessageService : IAutomaticMessageService
{
    public const int TitleMax = 100;
    public const int BodyMax = 1000;

    private readonly IApplicationDbContext _db;
    private readonly ILocalizationService _localization;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _user;
    private readonly IDateTimeProvider _clock;

    public AutomaticMessageService(
        IApplicationDbContext db,
        ILocalizationService localization,
        IAuditService audit,
        ICurrentUserService user,
        IDateTimeProvider clock)
    {
        _db = db;
        _localization = localization;
        _audit = audit;
        _user = user;
        _clock = clock;
    }

    public async Task<IReadOnlyList<AutomaticMessageDto>> ListAsync(CancellationToken ct = default)
    {
        var saved = await _db.NotificationMessageSettings.AsNoTracking().ToListAsync(ct);
        var byCode = saved.ToDictionary(s => s.Code, StringComparer.Ordinal);
        return AutomaticMessages.All.Select(d => Map(d, byCode.GetValueOrDefault(d.Code))).ToList();
    }

    public async Task<AutomaticMessageDto> UpdateAsync(
        string code, UpdateAutomaticMessageRequest request, CancellationToken ct = default)
    {
        var definition = Find(code);

        var titleAr = Clean(request.TitleAr, TitleMax, "العنوان العربي", definition);
        var bodyAr = Clean(request.BodyAr, BodyMax, "النص العربي", definition);
        var titleEn = Clean(request.TitleEn, TitleMax, "العنوان الإنجليزي", definition);
        var bodyEn = Clean(request.BodyEn, BodyMax, "النص الإنجليزي", definition);

        var row = await _db.NotificationMessageSettings.FirstOrDefaultAsync(s => s.Code == definition.Code, ct);
        var before = row is null ? null : Snapshot(row);
        var now = _clock.UtcNow;

        if (row is null)
        {
            row = new NotificationMessageSetting { Code = definition.Code, CreatedAt = now };
            _db.NotificationMessageSettings.Add(row);
        }

        row.Enabled = request.Enabled;
        row.TitleAr = titleAr;
        row.BodyAr = bodyAr;
        row.TitleEn = titleEn;
        row.BodyEn = bodyEn;
        row.UpdatedAt = now;
        row.UpdatedByUserId = _user.UserId;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.NotificationMessageChanged, "NotificationMessage", null,
            before, new { Code = definition.Code, After = Snapshot(row) }, ct: ct);

        return Map(definition, row);
    }

    public async Task<AutomaticMessageDto> ResetAsync(string code, CancellationToken ct = default)
    {
        var definition = Find(code);
        var row = await _db.NotificationMessageSettings.FirstOrDefaultAsync(s => s.Code == definition.Code, ct);
        if (row is not null)
        {
            var before = Snapshot(row);
            _db.NotificationMessageSettings.Remove(row);
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(AuditActionType.NotificationMessageChanged, "NotificationMessage", null,
                before, new { Code = definition.Code, Reset = true }, ct: ct);
        }

        return Map(definition, null);
    }

    private static AutomaticMessageDefinition Find(string code) =>
        AutomaticMessages.ForCode(code)
        ?? throw new DomainException(ErrorCodes.NotFound, "This automatic message does not exist.");

    private static string? Clean(string? value, int max, string field, AutomaticMessageDefinition definition)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        if (text.Length > max)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, $"{field}: الحد الأقصى {max} حرف.");
        }

        var unknown = AutomaticMessages.UnknownPlaceholders(text, definition);
        if (unknown.Count > 0)
        {
            var allowed = definition.Placeholders.Count == 0
                ? "هذه الرسالة لا تقبل أي متغيرات."
                : "المتاح: " + string.Join("، ", definition.Placeholders.Select(p => "{" + p.Token + "}"));
            throw new DomainException(ErrorCodes.ValidationFailed,
                $"{field}: المتغير {string.Join("، ", unknown.Select(t => "{" + t + "}"))} غير معروف. {allowed}");
        }

        return text;
    }

    private static object Snapshot(NotificationMessageSetting s) =>
        new { s.Enabled, s.TitleAr, s.BodyAr, s.TitleEn, s.BodyEn };

    private AutomaticMessageDto Map(AutomaticMessageDefinition d, NotificationMessageSetting? s)
    {
        AutomaticMessageTextDto Text(string lang, string? title, string? body) => new(
            title, body,
            AutomaticMessages.ToNamed(_localization.Translate(d.TitleKey, lang), d),
            AutomaticMessages.ToNamed(_localization.Translate(d.BodyKey, lang), d));

        return new AutomaticMessageDto(
            d.Code, d.Group, d.Name, d.When, d.Recipients, d.Placeholders,
            s?.Enabled ?? true,
            Text("ar", s?.TitleAr, s?.BodyAr),
            Text("en", s?.TitleEn, s?.BodyEn),
            s is not null && (!s.Enabled || s.TitleAr != null || s.BodyAr != null || s.TitleEn != null || s.BodyEn != null),
            s?.UpdatedAt);
    }
}
