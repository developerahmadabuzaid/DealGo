using System.Text.Json;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Infrastructure.Services;

public sealed class MoneyOperationQuery : PagedQuery
{
    /// <summary>Deposit or Withdrawal; null lists both.</summary>
    public PaymentType? Type { get; set; }
    public PaymentStatus? Status { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    /// <summary>A name, a phone number, or an operation's id, Paymob order or transaction number.</summary>
    public string? Search { get; set; }
}

public sealed record MoneyOperationRowDto(
    Guid Id,
    PaymentType Type,
    Guid UserId,
    string? FullName,
    string? PhoneNumber,
    decimal Amount,
    string Currency,
    PaymentStatus Status,
    string Provider,
    string? Reference,
    string? FailureReason,
    DateTime CreatedAt,
    DateTime? CompletedAt);

/// <summary>Totals for everything the filter matches, not only the page shown.</summary>
public sealed record MoneyOperationTotalsDto(
    int Count,
    decimal DepositedCompleted,
    int DepositsCompleted,
    decimal WithdrawnPaid,
    int WithdrawalsPaid,
    decimal WithdrawalsWaiting,
    int WithdrawalsWaitingCount,
    int Failed);

public sealed record MoneyOperationPageDto(
    PagedResult<MoneyOperationRowDto> Page,
    MoneyOperationTotalsDto Totals);

public sealed record LedgerLineDto(
    Guid Id,
    WalletType WalletType,
    WalletTransactionType TransactionType,
    decimal Amount,
    decimal BalanceBefore,
    decimal BalanceAfter,
    string? Description,
    DateTime CreatedAt);

/// <summary>
/// One message Paymob sent about the payment. The raw body is never returned: only what was
/// decided about it, and the card's brand and last four digits, which is all a card ever shows.
/// </summary>
public sealed record GatewayCallbackDto(
    Guid Id,
    DateTime ReceivedAt,
    bool SignatureValid,
    bool Processed,
    DateTime? ProcessedAt,
    string? ProcessingError,
    string? TransactionId,
    bool? Success);

public sealed record OperationHistoryDto(
    AuditActionType Action,
    string? ActorEmail,
    string? Reason,
    DateTime CreatedAt);

public sealed record MoneyOperationDetailDto(
    Guid Id,
    PaymentType Type,
    PaymentStatus Status,
    decimal Amount,
    string Currency,
    string Provider,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? FailureReason,

    Guid UserId,
    string? FullName,
    string? PhoneNumber,
    string? Email,
    decimal? MainBalance,

    long? GatewayOrderId,
    string? GatewayTransactionId,
    string? GatewayIntentionId,
    string? PaymentMethod,
    string? CardBrand,
    string? CardLast4,

    string? Iban,
    string? BankReference,

    string? IdempotencyKey,

    IReadOnlyList<LedgerLineDto> Ledger,
    IReadOnlyList<GatewayCallbackDto> Callbacks,
    IReadOnlyList<OperationHistoryDto> History);

/// <summary>
/// Every deposit and withdrawal, for the console: a list to search and filter, and the full story
/// of any one of them — who, how much, through what, which ledger lines it wrote, what Paymob
/// said about it, and who on the staff acted on it. Read only.
/// </summary>
public interface IMoneyOperationsService
{
    Task<MoneyOperationPageDto> ListAsync(MoneyOperationQuery query, CancellationToken ct = default);
    Task<MoneyOperationDetailDto> GetAsync(Guid id, CancellationToken ct = default);
}

public sealed class MoneyOperationsService : IMoneyOperationsService
{
    private const string IbanPrefix = "iban:";
    private const string IntentionPrefix = "paymob-intention:";
    private const string LegacyWithdrawalPrefix = "Wallet withdrawal to ";

    private readonly ApplicationDbContext _db;

    public MoneyOperationsService(ApplicationDbContext db) => _db = db;

    public async Task<MoneyOperationPageDto> ListAsync(MoneyOperationQuery query, CancellationToken ct = default)
    {
        var q = _db.Payments.AsNoTracking()
            .Where(p => p.PaymentType == PaymentType.Deposit || p.PaymentType == PaymentType.Withdrawal);

        if (query.Type is { } type) q = q.Where(p => p.PaymentType == type);
        if (query.Status is { } status) q = q.Where(p => p.Status == status);
        if (query.From is { } from) q = q.Where(p => p.CreatedAt >= from);
        // A date picked as "to" means the whole of that day.
        if (query.To is { } to) q = q.Where(p => p.CreatedAt < (to.TimeOfDay == TimeSpan.Zero ? to.AddDays(1) : to));

        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var phone = SaudiPhone.TryNormalise(search, out var normalised) ? normalised : null;
            var id = Guid.TryParse(search, out var g) ? g : (Guid?)null;
            var order = long.TryParse(search, out var o) ? o : (long?)null;
            var like = $"%{search}%";

            q = q.Where(p =>
                (id != null && (p.Id == id || p.UserId == id)) ||
                (order != null && p.ProviderOrderId == order) ||
                p.ProviderTransactionId == search ||
                (p.Metadata != null && EF.Functions.ILike(p.Metadata, like)) ||
                (p.User != null && (
                    EF.Functions.ILike(p.User.FullName, like) ||
                    (phone != null && p.User.PhoneNumber == phone) ||
                    (p.User.PhoneNumber != null && p.User.PhoneNumber.Contains(search)) ||
                    (p.User.Email != null && EF.Functions.ILike(p.User.Email, like)))));
        }

        var groups = await q
            .GroupBy(p => new { p.PaymentType, p.Status })
            .Select(g => new { g.Key.PaymentType, g.Key.Status, Count = g.Count(), Sum = g.Sum(p => p.Amount) })
            .ToListAsync(ct);

        decimal Sum(PaymentType t, params PaymentStatus[] s) =>
            groups.Where(g => g.PaymentType == t && s.Contains(g.Status)).Sum(g => g.Sum);
        int Count(PaymentType t, params PaymentStatus[] s) =>
            groups.Where(g => g.PaymentType == t && s.Contains(g.Status)).Sum(g => g.Count);

        var totals = new MoneyOperationTotalsDto(
            groups.Sum(g => g.Count),
            Money.Round(Sum(PaymentType.Deposit, PaymentStatus.Completed)),
            Count(PaymentType.Deposit, PaymentStatus.Completed),
            Money.Round(Sum(PaymentType.Withdrawal, PaymentStatus.Completed)),
            Count(PaymentType.Withdrawal, PaymentStatus.Completed),
            Money.Round(Sum(PaymentType.Withdrawal, PaymentStatus.Pending, PaymentStatus.Processing)),
            Count(PaymentType.Withdrawal, PaymentStatus.Pending, PaymentStatus.Processing),
            groups.Where(g => g.Status == PaymentStatus.Failed).Sum(g => g.Count));

        var total = totals.Count;
        var rows = await q.OrderByDescending(p => p.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(p => new
            {
                p.Id, p.PaymentType, p.UserId,
                FullName = p.User != null ? p.User.FullName : null,
                Phone = p.User != null ? p.User.PhoneNumber : null,
                p.Amount, p.Currency, p.Status, p.Provider, p.ProviderOrderId, p.ProviderTransactionId,
                p.Metadata, p.FailureReason, p.CreatedAt, p.CompletedAt
            })
            .ToListAsync(ct);

        var items = rows.Select(p => new MoneyOperationRowDto(
            p.Id, p.PaymentType, p.UserId, p.FullName, p.Phone, p.Amount, p.Currency, p.Status, p.Provider,
            p.PaymentType == PaymentType.Withdrawal
                ? IbanOf(p.Metadata)
                : p.ProviderOrderId?.ToString(),
            p.FailureReason, p.CreatedAt, p.CompletedAt)).ToList();

        return new MoneyOperationPageDto(
            PagedResult<MoneyOperationRowDto>.Create(items, query.Page, query.PageSize, total), totals);
    }

    public async Task<MoneyOperationDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var p = await _db.Payments.AsNoTracking()
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.Id == id &&
                (x.PaymentType == PaymentType.Deposit || x.PaymentType == PaymentType.Withdrawal), ct)
            ?? throw new DomainException(ErrorCodes.NotFound, "Operation not found.");

        // The ledger lines this operation wrote: a top-up's credit points at the payment, a
        // withdrawal's debit shares its idempotency key, and a refused withdrawal's refund points
        // back at it.
        var ledger = await _db.WalletTransactions.AsNoTracking()
            .Where(t => t.ReferenceId == p.Id ||
                        (p.IdempotencyKey != null && t.UserId == p.UserId && t.IdempotencyKey == p.IdempotencyKey))
            .OrderBy(t => t.CreatedAt)
            .Select(t => new LedgerLineDto(t.Id, t.WalletType, t.TransactionType, t.Amount,
                t.BalanceBefore, t.BalanceAfter, t.Description, t.CreatedAt))
            .ToListAsync(ct);

        var events = p.ProviderTransactionId == null || p.PaymentType != PaymentType.Deposit
            ? []
            : await _db.PaymentWebhookEvents.AsNoTracking()
                .Where(e => e.Provider == p.Provider && e.ProviderTransactionId == p.ProviderTransactionId)
                .OrderBy(e => e.ReceivedAt)
                .ToListAsync(ct);

        string? method = null, brand = null, last4 = null;
        var callbacks = new List<GatewayCallbackDto>();
        foreach (var e in events)
        {
            var card = ReadCard(e.Payload);
            if (e.SignatureValid && card.Type != null)
            {
                method = card.Type;
                brand = card.Brand;
                last4 = card.Last4;
            }

            callbacks.Add(new GatewayCallbackDto(e.Id, e.ReceivedAt, e.SignatureValid, e.Processed,
                e.ProcessedAt, e.ProcessingError, e.ProviderTransactionId, card.Success));
        }

        var history = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == p.Id)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new OperationHistoryDto(a.Action, a.ActorEmail, a.Reason, a.CreatedAt))
            .ToListAsync(ct);

        var balance = await _db.Wallets.AsNoTracking()
            .Where(w => w.UserId == p.UserId && w.WalletType == WalletType.Main)
            .Select(w => (decimal?)w.Balance)
            .FirstOrDefaultAsync(ct);

        var isWithdrawal = p.PaymentType == PaymentType.Withdrawal;
        var iban = isWithdrawal
            ? IbanOf(p.Metadata) ?? LegacyIban(ledger.FirstOrDefault(l => l.TransactionType == WalletTransactionType.Withdrawal)?.Description)
            : null;

        return new MoneyOperationDetailDto(
            p.Id, p.PaymentType, p.Status, p.Amount, p.Currency, p.Provider, p.CreatedAt, p.CompletedAt,
            p.FailureReason,
            p.UserId, p.User?.FullName, p.User?.PhoneNumber, p.User?.Email, balance,
            isWithdrawal ? null : p.ProviderOrderId,
            isWithdrawal ? null : p.ProviderTransactionId,
            p.Metadata?.StartsWith(IntentionPrefix) == true ? p.Metadata[IntentionPrefix.Length..] : null,
            method, brand, last4,
            iban,
            isWithdrawal && p.Status == PaymentStatus.Completed ? p.ProviderTransactionId : null,
            p.IdempotencyKey,
            ledger, callbacks, history);
    }

    private static string? IbanOf(string? metadata) =>
        metadata?.StartsWith(IbanPrefix) == true ? metadata[IbanPrefix.Length..] : null;

    private static string? LegacyIban(string? description) =>
        description?.StartsWith(LegacyWithdrawalPrefix) == true ? description[LegacyWithdrawalPrefix.Length..] : null;

    /// <summary>
    /// The payment method and card a Paymob callback describes. Paymob already masks the number;
    /// only its last four digits are kept, whatever it sent.
    /// </summary>
    private static (string? Type, string? Brand, string? Last4, bool? Success) ReadCard(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return default;

        string? type = null, brand = null, pan = null, success = null;
        try
        {
            if (payload.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(payload);
                var tx = doc.RootElement.TryGetProperty("obj", out var obj) ? obj : doc.RootElement;
                if (tx.TryGetProperty("source_data", out var source) && source.ValueKind == JsonValueKind.Object)
                {
                    type = Text(source, "type");
                    brand = Text(source, "sub_type");
                    pan = Text(source, "pan");
                }
                success = Text(tx, "success");
            }
            else
            {
                // The customer's return arrives as query parameters.
                var query = System.Web.HttpUtility.ParseQueryString(payload.TrimStart('?'));
                type = query["source_data.type"];
                brand = query["source_data.sub_type"];
                pan = query["source_data.pan"];
                success = query["success"];
            }
        }
        catch (JsonException)
        {
            return default;
        }

        var digits = pan is null ? null : new string(pan.Where(char.IsAsciiDigit).ToArray());
        var last4 = digits is { Length: >= 4 } ? digits[^4..] : null;

        return (Blank(type), Blank(brand), last4,
            success is null ? null : string.Equals(success, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => v.GetRawText()
            }
            : null;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
