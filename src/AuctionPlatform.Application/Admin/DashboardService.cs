using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Admin;

public sealed record DashboardDto(
    int ActiveAuctions,
    int UpcomingAuctions,
    int CompletedAuctions,
    long TotalParticipants,
    decimal TodayEntryRevenue,
    decimal TotalEntryRevenue,
    long PendingPrizePayments,
    decimal PendingPrizeValue,
    decimal ShoppingCreditIssued,
    decimal ShoppingCreditUsed,
    decimal ShoppingCreditOutstanding,
    long StoreOrders,
    decimal StoreRevenue,
    long PendingStoreOrders,
    decimal MainWalletDeposits,
    decimal MainWalletWithdrawals,
    string Currency);

public sealed record AuditLogDto(
    Guid Id,
    AuditActionType Action,
    Guid? ActorUserId,
    string? ActorEmail,
    string EntityType,
    Guid? EntityId,
    string? OldValue,
    string? NewValue,
    string? Reason,
    string? IpAddress,
    DateTime CreatedAt);

public sealed class AuditLogQuery : PagedQuery
{
    public AuditActionType? Action { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public sealed record AuctionEventDto(
    Guid Id,
    Guid AuctionId,
    Guid? UserId,
    AuctionEventType EventType,
    DateTime Timestamp,
    string? OldValue,
    string? NewValue,
    string? Metadata);

public interface IDashboardService
{
    Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default);
    Task<PagedResult<AuditLogDto>> GetAuditLogsAsync(AuditLogQuery query, CancellationToken ct = default);
    Task<PagedResult<AuctionEventDto>> GetAuctionEventsAsync(Guid auctionId, PagedQuery query, CancellationToken ct = default);
}

public sealed class DashboardService : IDashboardService
{
    private readonly IApplicationDbContext _db;
    private readonly IWalletService _wallets;
    private readonly IDateTimeProvider _clock;
    private readonly AuctionOptions _options;

    public DashboardService(
        IApplicationDbContext db,
        IWalletService wallets,
        IDateTimeProvider clock,
        Microsoft.Extensions.Options.IOptions<AuctionOptions> options)
    {
        _db = db;
        _wallets = wallets;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default)
    {
        var currency = _options.DefaultCurrency;
        var todayStart = _clock.UtcNow.Date;

        var activeAuctions = await _db.Auctions.AsNoTracking()
            .CountAsync(a => a.Status == AuctionStatus.Live || a.Status == AuctionStatus.FinalPhase, ct);

        var upcomingAuctions = await _db.Auctions.AsNoTracking()
            .CountAsync(a => a.Status == AuctionStatus.RegistrationOpen ||
                             a.Status == AuctionStatus.WaitingForParticipants ||
                             a.Status == AuctionStatus.Ready ||
                             a.Status == AuctionStatus.WaitingForStart, ct);

        var completedAuctions = await _db.Auctions.AsNoTracking()
            .CountAsync(a => a.Status == AuctionStatus.Completed ||
                             a.Status == AuctionStatus.WinnersDeclared ||
                             a.Status == AuctionStatus.PrizeClaim ||
                             a.Status == AuctionStatus.Finished, ct);

        var totalParticipants = await _db.AuctionParticipants.AsNoTracking().LongCountAsync(ct);

        var entryFees = _db.WalletTransactions.AsNoTracking()
            .Where(t => t.TransactionType == WalletTransactionType.AuctionEntryFee);

        var totalEntryRevenue = Math.Abs(await entryFees.SumAsync(t => (decimal?)t.Amount, ct) ?? 0m);

        var todayEntryRevenue = Math.Abs(await entryFees
            .Where(t => t.CreatedAt >= todayStart)
            .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m);

        var pendingClaims = await _db.PrizeClaims.AsNoTracking()
            .Where(c => c.Status == PrizeClaimStatus.PaymentRequired || c.Status == PrizeClaimStatus.Pending)
            .Select(c => c.PaymentAmount)
            .ToListAsync(ct);

        var credit = await _wallets.GetShoppingCreditSummaryAsync(currency, ct);

        var storeOrders = await _db.StoreOrders.AsNoTracking().LongCountAsync(ct);

        var pendingStoreOrders = await _db.StoreOrders.AsNoTracking()
            .LongCountAsync(o => o.Status == StoreOrderStatus.Pending ||
                                 o.Status == StoreOrderStatus.Paid ||
                                 o.Status == StoreOrderStatus.Processing, ct);

        var storeRevenue = await _db.StoreOrders.AsNoTracking()
            .Where(o => o.Status != StoreOrderStatus.Cancelled && o.Status != StoreOrderStatus.Refunded)
            .SumAsync(o => (decimal?)o.Total, ct) ?? 0m;

        var deposits = await _db.WalletTransactions.AsNoTracking()
            .Where(t => t.TransactionType == WalletTransactionType.Deposit)
            .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;

        var withdrawals = Math.Abs(await _db.WalletTransactions.AsNoTracking()
            .Where(t => t.TransactionType == WalletTransactionType.Withdrawal)
            .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m);

        return new DashboardDto(
            activeAuctions, upcomingAuctions, completedAuctions, totalParticipants,
            Money.Round(todayEntryRevenue), Money.Round(totalEntryRevenue),
            pendingClaims.Count, Money.Round(pendingClaims.Sum()),
            credit.TotalIssued, credit.TotalUsed, credit.Outstanding,
            storeOrders, Money.Round(storeRevenue), pendingStoreOrders,
            Money.Round(deposits), Money.Round(withdrawals), currency);
    }

    public async Task<PagedResult<AuditLogDto>> GetAuditLogsAsync(
        AuditLogQuery query, CancellationToken ct = default)
    {
        var q = _db.AuditLogs.AsNoTracking().AsQueryable();

        if (query.Action.HasValue) q = q.Where(a => a.Action == query.Action.Value);
        if (query.ActorUserId.HasValue) q = q.Where(a => a.ActorUserId == query.ActorUserId.Value);
        if (query.EntityId.HasValue) q = q.Where(a => a.EntityId == query.EntityId.Value);
        if (query.From.HasValue) q = q.Where(a => a.CreatedAt >= query.From.Value);
        if (query.To.HasValue) q = q.Where(a => a.CreatedAt <= query.To.Value);

        if (!string.IsNullOrWhiteSpace(query.EntityType))
            q = q.Where(a => a.EntityType == query.EntityType);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(a => (a.ActorEmail != null && a.ActorEmail.ToLower().Contains(term)) ||
                             (a.Reason != null && a.Reason.ToLower().Contains(term)));
        }

        var total = await q.LongCountAsync(ct);

        var items = await q
            .OrderByDescending(a => a.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(a => new AuditLogDto(
                a.Id, a.Action, a.ActorUserId, a.ActorEmail, a.EntityType, a.EntityId,
                a.OldValue, a.NewValue, a.Reason, a.IpAddress, a.CreatedAt))
            .ToListAsync(ct);

        return PagedResult<AuditLogDto>.Create(items, query.Page, query.PageSize, total);
    }

    public async Task<PagedResult<AuctionEventDto>> GetAuctionEventsAsync(
        Guid auctionId, PagedQuery query, CancellationToken ct = default)
    {
        var q = _db.AuctionEvents.AsNoTracking().Where(e => e.AuctionId == auctionId);
        var total = await q.LongCountAsync(ct);

        var items = await q
            .OrderByDescending(e => e.Timestamp)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(e => new AuctionEventDto(
                e.Id, e.AuctionId, e.UserId, e.EventType, e.Timestamp,
                e.OldValue, e.NewValue, e.Metadata))
            .ToListAsync(ct);

        return PagedResult<AuctionEventDto>.Create(items, query.Page, query.PageSize, total);
    }
}
