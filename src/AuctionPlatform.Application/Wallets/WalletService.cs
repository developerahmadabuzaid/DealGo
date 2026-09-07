using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Application.Wallets;

public sealed class WalletService : IWalletService
{
    private readonly IApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly AuctionOptions _options;
    private readonly ILogger<WalletService> _logger;

    public WalletService(
        IApplicationDbContext db,
        IDateTimeProvider clock,
        IOptions<AuctionOptions> options,
        ILogger<WalletService> logger)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WalletsDto> GetWalletsAsync(Guid userId, CancellationToken ct = default)
    {
        await EnsureWalletsExistAsync(userId, _options.DefaultCurrency, ct);

        var wallets = await _db.Wallets
            .AsNoTracking()
            .Where(w => w.UserId == userId)
            .ToListAsync(ct);

        var main = wallets.First(w => w.WalletType == WalletType.Main);
        var shopping = wallets.First(w => w.WalletType == WalletType.Shopping);

        return new WalletsDto(Map(main), Map(shopping), main.Currency);
    }

    private static WalletDto Map(Wallet w) =>
        new(w.Id, w.WalletType, w.Balance, w.Currency, WalletRules.IsWithdrawable(w.WalletType), w.UpdatedAt);

    public async Task EnsureWalletsExistAsync(Guid userId, string currency, CancellationToken ct = default)
    {
        var existing = await _db.Wallets
            .Where(w => w.UserId == userId)
            .Select(w => w.WalletType)
            .ToListAsync(ct);

        var missing = new[] { WalletType.Main, WalletType.Shopping }
            .Where(t => !existing.Contains(t))
            .ToList();

        if (missing.Count == 0) return;

        foreach (var type in missing)
        {
            _db.Wallets.Add(new Wallet
            {
                UserId = userId,
                WalletType = type,
                Balance = 0m,
                Currency = currency,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent request created the same wallets first; the unique index did its job.
            _logger.LogDebug("Wallet creation raced for user {UserId}; using the existing rows.", userId);
        }
    }

    public Task<WalletOperationResult> CreditAsync(WalletMovement movement, CancellationToken ct = default) =>
        MoveAsync(movement, isCredit: true, ct);

    public Task<WalletOperationResult> DebitAsync(WalletMovement movement, CancellationToken ct = default) =>
        MoveAsync(movement, isCredit: false, ct);

    /// <summary>
    /// Locks the wallet row with SELECT ... FOR UPDATE, applies the delta and writes the ledger
    /// entry. Two concurrent movements against one wallet therefore serialise, which is what stops
    /// the same balance being spent twice.
    /// </summary>
    private async Task<WalletOperationResult> MoveAsync(WalletMovement movement, bool isCredit, CancellationToken ct)
    {
        WalletRules.EnsurePositiveAmount(movement.Amount);
        var amount = Money.Round(movement.Amount);

        if (movement.IdempotencyKey is { Length: > 0 })
        {
            var existing = await _db.WalletTransactions
                .AsNoTracking()
                .FirstOrDefaultAsync(t =>
                    t.UserId == movement.UserId &&
                    t.IdempotencyKey == movement.IdempotencyKey, ct);

            if (existing is not null)
            {
                return new WalletOperationResult(
                    existing.WalletId, existing.WalletType, existing.BalanceBefore,
                    existing.BalanceAfter, Math.Abs(existing.Amount), existing.Currency,
                    existing.Id, WasDuplicate: true);
            }
        }

        await EnsureWalletsExistAsync(movement.UserId, movement.Currency, ct);

        // SELECT ... FOR UPDATE only holds its lock for the life of a transaction, so a movement
        // called on its own must open one. When the caller already has a transaction open this
        // joins it, and the returned handle's Commit is a no-op.
        await using var tx = await _db.BeginTransactionAsync(ct);

        // Row-level lock. Ordering by type keeps multi-wallet operations deadlock-free.
        var wallet = await _db.Wallets
            .FromSqlRaw(
                """
                SELECT *, xmin FROM "Wallets"
                WHERE "UserId" = {0} AND "WalletType" = {1}
                FOR UPDATE
                """,
                movement.UserId, (int)movement.WalletType)
            .FirstOrDefaultAsync(ct);

        if (wallet is null)
        {
            throw new DomainException(ErrorCodes.WalletNotFound, "Wallet not found for this user.");
        }

        WalletRules.EnsureSameCurrency(wallet.Currency, movement.Currency);

        var before = wallet.Balance;
        decimal after;

        if (isCredit)
        {
            after = Money.Round(before + amount);
        }
        else
        {
            WalletRules.EnsureSufficientBalance(before, amount, wallet.WalletType);
            after = Money.Round(before - amount);
        }

        wallet.Balance = after;
        wallet.UpdatedAt = _clock.UtcNow;

        var transaction = new WalletTransaction
        {
            WalletId = wallet.Id,
            UserId = movement.UserId,
            WalletType = movement.WalletType,
            TransactionType = movement.TransactionType,
            Amount = isCredit ? amount : -amount,
            Currency = movement.Currency,
            BalanceBefore = before,
            BalanceAfter = after,
            ReferenceType = movement.ReferenceType,
            ReferenceId = movement.ReferenceId,
            CreditSource = movement.CreditSource,
            ExpiresAt = movement.ExpiresAt,
            Description = movement.Description,
            Metadata = movement.Metadata,
            IdempotencyKey = movement.IdempotencyKey,
            CreatedAt = _clock.UtcNow
        };

        _db.WalletTransactions.Add(transaction);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Wallet {WalletType} for user {UserId} moved {Amount} {Currency} ({Type}); {Before} -> {After}",
            movement.WalletType, movement.UserId, transaction.Amount, movement.Currency,
            movement.TransactionType, before, after);

        return new WalletOperationResult(
            wallet.Id, wallet.WalletType, before, after, amount, wallet.Currency,
            transaction.Id, WasDuplicate: false);
    }

    public async Task<PagedResult<WalletTransactionDto>> GetTransactionsAsync(
        Guid userId, WalletTransactionQuery query, CancellationToken ct = default)
    {
        var q = _db.WalletTransactions.AsNoTracking().Where(t => t.UserId == userId);

        if (query.WalletType.HasValue)
            q = q.Where(t => t.WalletType == query.WalletType.Value);

        if (query.TransactionType.HasValue)
            q = q.Where(t => t.TransactionType == query.TransactionType.Value);

        if (query.From.HasValue)
            q = q.Where(t => t.CreatedAt >= query.From.Value);

        if (query.To.HasValue)
            q = q.Where(t => t.CreatedAt <= query.To.Value);

        var total = await q.LongCountAsync(ct);

        var items = await q
            .OrderByDescending(t => t.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(t => new WalletTransactionDto(
                t.Id, t.WalletType, t.TransactionType, t.Amount, t.Currency,
                t.BalanceBefore, t.BalanceAfter, t.ReferenceType, t.ReferenceId,
                t.CreditSource, t.ExpiresAt, t.Description, t.CreatedAt))
            .ToListAsync(ct);

        return PagedResult<WalletTransactionDto>.Create(items, query.Page, query.PageSize, total);
    }

    public async Task<WalletOperationResult> DepositAsync(Guid userId, DepositRequest request, CancellationToken ct = default)
    {
        var currency = request.Currency ?? _options.DefaultCurrency;

        await using var tx = await _db.BeginTransactionAsync(ct);

        var result = await CreditAsync(new WalletMovement
        {
            UserId = userId,
            WalletType = WalletType.Main,
            Amount = request.Amount,
            Currency = currency,
            TransactionType = WalletTransactionType.Deposit,
            ReferenceType = "Deposit",
            Description = "Wallet deposit",
            IdempotencyKey = request.IdempotencyKey
        }, ct);

        if (!result.WasDuplicate)
        {
            _db.Payments.Add(new Payment
            {
                UserId = userId,
                PaymentType = PaymentType.Deposit,
                Amount = Money.Round(request.Amount),
                Currency = currency,
                Provider = request.Provider ?? "SANDBOX",
                Status = PaymentStatus.Completed,
                IdempotencyKey = request.IdempotencyKey,
                CreatedAt = _clock.UtcNow,
                CompletedAt = _clock.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<WalletOperationResult> WithdrawAsync(Guid userId, WithdrawRequest request, CancellationToken ct = default)
    {
        var currency = request.Currency ?? _options.DefaultCurrency;

        // Withdrawals are Main-wallet only. Shopping credit is never part of a withdrawable total.
        WalletRules.EnsureWithdrawable(WalletType.Main);

        await using var tx = await _db.BeginTransactionAsync(ct);

        var result = await DebitAsync(new WalletMovement
        {
            UserId = userId,
            WalletType = WalletType.Main,
            Amount = request.Amount,
            Currency = currency,
            TransactionType = WalletTransactionType.Withdrawal,
            ReferenceType = "Withdrawal",
            Description = request.Destination is null
                ? "Wallet withdrawal"
                : $"Wallet withdrawal to {request.Destination}",
            IdempotencyKey = request.IdempotencyKey
        }, ct);

        if (!result.WasDuplicate)
        {
            _db.Payments.Add(new Payment
            {
                UserId = userId,
                PaymentType = PaymentType.Withdrawal,
                Amount = Money.Round(request.Amount),
                Currency = currency,
                Provider = "SANDBOX",
                Status = PaymentStatus.Processing,
                IdempotencyKey = request.IdempotencyKey,
                CreatedAt = _clock.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<ShoppingCreditSummaryDto> GetShoppingCreditSummaryAsync(string currency, CancellationToken ct = default)
    {
        var credits = await _db.WalletTransactions
            .AsNoTracking()
            .Where(t => t.WalletType == WalletType.Shopping && t.Currency == currency && t.Amount > 0)
            .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;

        var debits = await _db.WalletTransactions
            .AsNoTracking()
            .Where(t => t.WalletType == WalletType.Shopping && t.Currency == currency && t.Amount < 0)
            .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;

        var used = Math.Abs(debits);

        return new ShoppingCreditSummaryDto(
            Money.Round(credits),
            Money.Round(used),
            Money.Round(credits - used),
            currency);
    }
}
