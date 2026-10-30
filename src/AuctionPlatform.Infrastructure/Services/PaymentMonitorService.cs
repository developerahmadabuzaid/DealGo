using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Infrastructure.Services;

public sealed record PaymentHealthDto(
    DateTime GeneratedAt,
    bool PaymobConfigured,
    string PaymobMode,
    bool PaymobCanInquire,
    bool LedgerGuardsInstalled,
    int TopUpsCompleted24h,
    decimal TopUpsCompletedAmount24h,
    int TopUpsFailed24h,
    int TopUpsOpen,
    int TopUpsStuck,
    int CallbacksUnprocessed,
    int CallbacksRejected24h,
    int CallbacksUnmatched7d,
    int AmountMismatches,
    int RefundedAfterCredit,
    int FailedEntryFeeRefunds,
    int WalletsOutOfBalance,
    IReadOnlyList<Guid> WalletsOutOfBalanceSample);

public sealed record TopUpRowDto(
    Guid PaymentId,
    Guid UserId,
    string? UserName,
    string? Phone,
    decimal Amount,
    string Currency,
    PaymentStatus Status,
    long? PaymobOrderId,
    string? PaymobTransactionId,
    string? FailureReason,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public interface IPaymentMonitorService
{
    Task<PaymentHealthDto> GetHealthAsync(CancellationToken ct = default);

    /// <summary>Every Paymob top-up in a period, to set beside Paymob's own transaction report.</summary>
    Task<PagedResult<TopUpRowDto>> ListTopUpsAsync(
        DateTime? from, DateTime? to, PaymentStatus? status, PagedQuery page, CancellationToken ct = default);
}

/// <summary>
/// The figures a person needs to trust the money: what Paymob paid in, what is stuck, what was
/// refused, and whether every wallet still equals the sum of its ledger.
/// </summary>
public sealed class PaymentMonitorService : IPaymentMonitorService
{
    private readonly ApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly PaymobOptions _paymob;

    public PaymentMonitorService(ApplicationDbContext db, IDateTimeProvider clock, IOptions<PaymobOptions> paymob)
    {
        _db = db;
        _clock = clock;
        _paymob = paymob.Value;
    }

    public async Task<PaymentHealthDto> GetHealthAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var day = now.AddHours(-24);
        var week = now.AddDays(-7);
        var stuckBefore = now.AddMinutes(-15);
        const string P = WalletTopUpService.Provider;

        var topUps = _db.Payments.AsNoTracking().Where(p => p.Provider == P && p.PaymentType == PaymentType.Deposit);

        var completed = await topUps.Where(p => p.Status == PaymentStatus.Completed && p.CompletedAt >= day)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Sum = g.Sum(p => p.Amount) })
            .FirstOrDefaultAsync(ct);

        var failed = await topUps.CountAsync(p => p.Status == PaymentStatus.Failed && p.CreatedAt >= day, ct);
        var open = await topUps.CountAsync(p => p.Status == PaymentStatus.Processing || p.Status == PaymentStatus.Pending, ct);
        var stuck = await topUps.CountAsync(p =>
            (p.Status == PaymentStatus.Processing || p.Status == PaymentStatus.Pending) && p.CreatedAt < stuckBefore, ct);
        var mismatches = await topUps.CountAsync(p =>
            p.FailureReason != null && (p.FailureReason.StartsWith(WalletTopUpService.AmountMismatch) ||
                                        p.FailureReason.StartsWith(WalletTopUpService.ReferenceMismatch)), ct);

        var events = _db.PaymentWebhookEvents.AsNoTracking().Where(e => e.Provider == P);
        var unprocessed = await events.CountAsync(e => e.SignatureValid && !e.Processed, ct);
        var rejected = await events.CountAsync(e => !e.SignatureValid && e.ReceivedAt >= day, ct);
        var unmatched = await events.CountAsync(e =>
            e.ProcessingError != null && e.ProcessingError.StartsWith(WalletTopUpService.NoMatch) && e.ReceivedAt >= week, ct);
        var refundedAfter = await events.CountAsync(e =>
            e.ProcessingError != null && e.ProcessingError.StartsWith(WalletTopUpService.RefundedAfterCredit), ct);

        var failedRefunds = await _db.AuctionEntryFeeRefunds.AsNoTracking().CountAsync(r => r.Status == RefundStatus.Failed, ct);

        // A wallet must equal the sum of its ledger. Anything else means money moved without an
        // entry (or an entry without money), which must never happen.
        var outOfBalance = await _db.Database
            .SqlQueryRaw<Guid>("""
                SELECT w."Id" AS "Value"
                FROM "Wallets" w
                LEFT JOIN (SELECT "WalletId", SUM("Amount") AS total FROM "WalletTransactions" GROUP BY "WalletId") t
                       ON t."WalletId" = w."Id"
                WHERE w."Balance" <> COALESCE(t.total, 0)
                """)
            .ToListAsync(ct);

        var guards = await _db.Database
            .SqlQueryRaw<int>("""
                SELECT COUNT(*)::int AS "Value" FROM pg_trigger
                WHERE tgname IN ('trg_wallet_ledger_guard', 'trg_payment_guard') AND NOT tgisinternal
                """)
            .FirstAsync(ct);

        return new PaymentHealthDto(
            now,
            _paymob.IsConfigured,
            _paymob.IsConfigured ? _paymob.Mode.ToLowerInvariant() : "off",
            _paymob.CanInquire,
            guards == 2,
            completed?.Count ?? 0,
            Money.Round(completed?.Sum ?? 0m),
            failed,
            open,
            stuck,
            unprocessed,
            rejected,
            unmatched,
            mismatches,
            refundedAfter,
            failedRefunds,
            outOfBalance.Count,
            outOfBalance.Take(20).ToList());
    }

    public async Task<PagedResult<TopUpRowDto>> ListTopUpsAsync(
        DateTime? from, DateTime? to, PaymentStatus? status, PagedQuery page, CancellationToken ct = default)
    {
        var q = _db.Payments.AsNoTracking()
            .Where(p => p.Provider == WalletTopUpService.Provider && p.PaymentType == PaymentType.Deposit);
        if (from is { } f) q = q.Where(p => p.CreatedAt >= f);
        if (to is { } t) q = q.Where(p => p.CreatedAt <= t);
        if (status is { } s) q = q.Where(p => p.Status == s);

        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(p => p.CreatedAt)
            .Skip(page.Skip).Take(page.PageSize)
            .Select(p => new TopUpRowDto(
                p.Id, p.UserId,
                p.User != null ? p.User.FullName : null,
                p.User != null ? p.User.PhoneNumber : null,
                p.Amount, p.Currency, p.Status, p.ProviderOrderId, p.ProviderTransactionId,
                p.FailureReason, p.CreatedAt, p.CompletedAt))
            .ToListAsync(ct);

        return PagedResult<TopUpRowDto>.Create(rows, page.Page, page.PageSize, total);
    }
}
