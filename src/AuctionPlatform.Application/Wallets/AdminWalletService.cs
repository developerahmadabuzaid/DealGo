using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Wallets;

public sealed record AdminWalletRowDto(
    Guid UserId,
    string FullName,
    string? Email,
    decimal MainBalance,
    decimal ShoppingBalance,
    string Currency,
    decimal TotalAuctionRefunds,
    decimal TotalStorePurchases,
    DateTime? LastTransactionAt);

public sealed class AdminWalletQuery : PagedQuery
{
    public WalletType? WalletType { get; set; }
    public decimal? MinBalance { get; set; }
}

public sealed record ShoppingCreditByUserDto(
    Guid UserId, string FullName, string? Email,
    decimal Issued, decimal Used, decimal Remaining, string Currency);

public interface IAdminWalletService
{
    Task<PagedResult<AdminWalletRowDto>> ListAsync(AdminWalletQuery query, CancellationToken ct = default);
    Task<AdminWalletRowDto> GetAsync(Guid userId, CancellationToken ct = default);
    Task<PagedResult<ShoppingCreditByUserDto>> GetShoppingCreditsAsync(PagedQuery query, CancellationToken ct = default);

    /// <summary>
    /// Manually credits or debits a wallet. Requires an explicit reason and always produces an
    /// audit record; this is the only sanctioned way for staff to move a user's balance.
    /// </summary>
    Task<WalletOperationResult> AdjustAsync(Guid userId, AdminCreditRequest request, CancellationToken ct = default);
}

public sealed class AdminWalletService : IAdminWalletService
{
    private readonly IApplicationDbContext _db;
    private readonly IWalletService _wallets;
    private readonly IAuditService _audit;

    public AdminWalletService(IApplicationDbContext db, IWalletService wallets, IAuditService audit)
    {
        _db = db;
        _wallets = wallets;
        _audit = audit;
    }

    public async Task<PagedResult<AdminWalletRowDto>> ListAsync(
        AdminWalletQuery query, CancellationToken ct = default)
    {
        var users = _db.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            users = users.Where(u => u.FullName.ToLower().Contains(term) ||
                                     (u.Email != null && u.Email.ToLower().Contains(term)));
        }

        var total = await users.LongCountAsync(ct);

        var page = await users
            .OrderByDescending(u => u.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(u => new { u.Id, u.FullName, u.Email })
            .ToListAsync(ct);

        var ids = page.Select(u => u.Id).ToList();
        var rows = await BuildRowsAsync(ids, ct);

        var items = page
            .Select(u => rows.GetValueOrDefault(u.Id) ?? new AdminWalletRowDto(
                u.Id, u.FullName, u.Email, 0m, 0m, Money.DefaultCurrency, 0m, 0m, null))
            .ToList();

        return PagedResult<AdminWalletRowDto>.Create(items, query.Page, query.PageSize, total);
    }

    private async Task<Dictionary<Guid, AdminWalletRowDto>> BuildRowsAsync(
        List<Guid> userIds, CancellationToken ct)
    {
        var users = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Email })
            .ToListAsync(ct);

        var wallets = await _db.Wallets.AsNoTracking()
            .Where(w => userIds.Contains(w.UserId))
            .Select(w => new { w.UserId, w.WalletType, w.Balance, w.Currency })
            .ToListAsync(ct);

        var aggregates = await _db.WalletTransactions.AsNoTracking()
            .Where(t => userIds.Contains(t.UserId))
            .GroupBy(t => t.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Refunds = g.Where(t => t.CreditSource == ShoppingCreditSource.AuctionEntryFeeRefund)
                           .Sum(t => (decimal?)t.Amount) ?? 0m,
                Purchases = g.Where(t => t.TransactionType == WalletTransactionType.StorePurchase)
                             .Sum(t => (decimal?)t.Amount) ?? 0m,
                Last = g.Max(t => (DateTime?)t.CreatedAt)
            })
            .ToListAsync(ct);

        return users.ToDictionary(u => u.Id, u =>
        {
            var main = wallets.FirstOrDefault(w => w.UserId == u.Id && w.WalletType == WalletType.Main);
            var shopping = wallets.FirstOrDefault(w => w.UserId == u.Id && w.WalletType == WalletType.Shopping);
            var agg = aggregates.FirstOrDefault(a => a.UserId == u.Id);

            return new AdminWalletRowDto(
                u.Id, u.FullName, u.Email,
                main?.Balance ?? 0m, shopping?.Balance ?? 0m,
                main?.Currency ?? Money.DefaultCurrency,
                agg?.Refunds ?? 0m, Math.Abs(agg?.Purchases ?? 0m), agg?.Last);
        });
    }

    public async Task<AdminWalletRowDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await BuildRowsAsync(new List<Guid> { userId }, ct);
        return rows.GetValueOrDefault(userId)
            ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");
    }

    public async Task<PagedResult<ShoppingCreditByUserDto>> GetShoppingCreditsAsync(
        PagedQuery query, CancellationToken ct = default)
    {
        var grouped = _db.WalletTransactions.AsNoTracking()
            .Where(t => t.WalletType == WalletType.Shopping)
            .GroupBy(t => t.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Issued = g.Where(t => t.Amount > 0).Sum(t => (decimal?)t.Amount) ?? 0m,
                Used = g.Where(t => t.Amount < 0).Sum(t => (decimal?)t.Amount) ?? 0m,
                Currency = g.Max(t => t.Currency)
            });

        var total = await grouped.LongCountAsync(ct);

        var page = await grouped
            .OrderByDescending(x => x.Issued)
            .Skip(query.Skip).Take(query.PageSize)
            .ToListAsync(ct);

        var ids = page.Select(p => p.UserId).ToList();

        var users = await _db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Email })
            .ToDictionaryAsync(u => u.Id, ct);

        var items = page.Select(p =>
        {
            var user = users.GetValueOrDefault(p.UserId);
            var used = Math.Abs(p.Used);

            return new ShoppingCreditByUserDto(
                p.UserId, user?.FullName ?? string.Empty, user?.Email,
                Money.Round(p.Issued), Money.Round(used), Money.Round(p.Issued - used),
                p.Currency ?? Money.DefaultCurrency);
        }).ToList();

        return PagedResult<ShoppingCreditByUserDto>.Create(items, query.Page, query.PageSize, total);
    }

    public async Task<WalletOperationResult> AdjustAsync(
        Guid userId, AdminCreditRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed, "A reason is required for manual wallet adjustments.");
        }

        if (request.Amount == 0)
        {
            throw new DomainException(ErrorCodes.InvalidAmount, "The adjustment amount cannot be zero.");
        }

        // Without a key, a double click or a retried request adjusts the wallet twice.
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed, "An idempotency key is required for manual wallet adjustments.");
        }

        var currency = request.Currency ?? Money.DefaultCurrency;
        var isCredit = request.Amount > 0;

        var movement = new WalletMovement
        {
            UserId = userId,
            WalletType = request.WalletType,
            Amount = Math.Abs(request.Amount),
            Currency = currency,
            TransactionType = isCredit && request.WalletType == WalletType.Shopping
                ? WalletTransactionType.ShoppingCredit
                : WalletTransactionType.Adjustment,
            ReferenceType = "AdminAdjustment",
            CreditSource = request.WalletType == WalletType.Shopping && isCredit
                ? ShoppingCreditSource.AdminAdjustment
                : null,
            Description = request.Reason,
            IdempotencyKey = request.IdempotencyKey
        };

        await using var tx = await _db.BeginTransactionAsync(ct);

        var result = isCredit
            ? await _wallets.CreditAsync(movement, ct)
            : await _wallets.DebitAsync(movement, ct);

        // Inside the same transaction as the money: an adjustment is never made without its audit
        // record, and a retried one (same key) is not audited twice.
        if (!result.WasDuplicate)
        {
            await _audit.LogAsync(
                request.WalletType == WalletType.Shopping
                    ? AuditActionType.ShoppingCreditIssued
                    : AuditActionType.WalletAdjusted,
                nameof(Wallet), result.WalletId,
                new { Balance = result.BalanceBefore },
                new { Balance = result.BalanceAfter, request.Amount, WalletType = request.WalletType.ToString() },
                request.Reason, ct);
        }

        await tx.CommitAsync(ct);

        return result;
    }
}
