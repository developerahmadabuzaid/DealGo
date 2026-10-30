using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Wallets;

public sealed record AdminWithdrawalDto(
    Guid Id,
    Guid UserId,
    string FullName,
    string? PhoneNumber,
    string? Email,
    decimal Amount,
    string Currency,
    string? Iban,
    PaymentStatus Status,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? BankReference,
    string? FailureReason);

public sealed class AdminWithdrawalQuery : PagedQuery
{
    /// <summary>Null lists every withdrawal; the console opens on the ones still to be paid.</summary>
    public PaymentStatus? Status { get; set; }
}

public sealed record MarkWithdrawalPaidRequest(string BankReference);

public sealed record RejectWithdrawalRequest(string Reason);

/// <summary>
/// The queue of withdrawals waiting for a bank transfer.
///
/// A withdrawal takes the money out of the main wallet when the user asks for it, and waits here
/// as Processing. Staff make the transfer from the company account, then either mark it paid with
/// the bank's reference, or refuse it, which puts the full amount back in the user's main wallet.
/// Each withdrawal can be settled once: the payment row is locked and its status checked first.
/// </summary>
public interface IAdminWithdrawalService
{
    Task<PagedResult<AdminWithdrawalDto>> ListAsync(AdminWithdrawalQuery query, CancellationToken ct = default);
    Task<AdminWithdrawalDto> MarkPaidAsync(Guid paymentId, MarkWithdrawalPaidRequest request, CancellationToken ct = default);
    Task<AdminWithdrawalDto> RejectAsync(Guid paymentId, RejectWithdrawalRequest request, CancellationToken ct = default);
}

public sealed class AdminWithdrawalService : IAdminWithdrawalService
{
    private const string IbanPrefix = "iban:";
    private const string LegacyDescriptionPrefix = "Wallet withdrawal to ";

    private readonly IApplicationDbContext _db;
    private readonly IWalletService _wallets;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;

    public AdminWithdrawalService(
        IApplicationDbContext db, IWalletService wallets, IAuditService audit, IDateTimeProvider clock)
    {
        _db = db;
        _wallets = wallets;
        _audit = audit;
        _clock = clock;
    }

    public async Task<PagedResult<AdminWithdrawalDto>> ListAsync(
        AdminWithdrawalQuery query, CancellationToken ct = default)
    {
        var withdrawals = _db.Payments.AsNoTracking().Where(p => p.PaymentType == PaymentType.Withdrawal);

        if (query.Status is { } status) withdrawals = withdrawals.Where(p => p.Status == status);

        var total = await withdrawals.LongCountAsync(ct);

        // Oldest first: the queue is worked through in the order people asked.
        var page = await withdrawals
            .OrderBy(p => p.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .ToListAsync(ct);

        var items = await MapAsync(page, ct);
        return PagedResult<AdminWithdrawalDto>.Create(items, query.Page, query.PageSize, total);
    }

    public async Task<AdminWithdrawalDto> MarkPaidAsync(
        Guid paymentId, MarkWithdrawalPaidRequest request, CancellationToken ct = default)
    {
        var reference = request.BankReference?.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed, "Enter the bank transfer reference.");
        }

        await using var tx = await _db.BeginTransactionAsync(ct);

        var payment = await LockPendingAsync(paymentId, ct);

        payment.Status = PaymentStatus.Completed;
        payment.ProviderTransactionId = reference;
        payment.CompletedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            AuditActionType.WithdrawalPaid, nameof(Payment), payment.Id,
            new { Status = PaymentStatus.Processing.ToString() },
            new { Status = payment.Status.ToString(), payment.Amount, payment.Currency, BankReference = reference },
            $"Bank transfer {reference}", ct);

        await tx.CommitAsync(ct);

        return (await MapAsync(new List<Payment> { payment }, ct))[0];
    }

    public async Task<AdminWithdrawalDto> RejectAsync(
        Guid paymentId, RejectWithdrawalRequest request, CancellationToken ct = default)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A reason is required.");
        }

        await using var tx = await _db.BeginTransactionAsync(ct);

        var payment = await LockPendingAsync(paymentId, ct);

        // The key is the withdrawal's own id, so the amount can only ever go back once.
        await _wallets.CreditAsync(new WalletMovement
        {
            UserId = payment.UserId,
            WalletType = WalletType.Main,
            Amount = payment.Amount,
            Currency = payment.Currency,
            TransactionType = WalletTransactionType.Refund,
            ReferenceType = "Withdrawal",
            ReferenceId = payment.Id,
            Description = $"Withdrawal refused: {reason}",
            IdempotencyKey = $"withdrawal-refused:{payment.Id:N}"
        }, ct);

        payment.Status = PaymentStatus.Cancelled;
        payment.FailureReason = reason;
        payment.CompletedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            AuditActionType.WithdrawalRejected, nameof(Payment), payment.Id,
            new { Status = PaymentStatus.Processing.ToString() },
            new { Status = payment.Status.ToString(), payment.Amount, payment.Currency, Refunded = true },
            reason, ct);

        await tx.CommitAsync(ct);

        return (await MapAsync(new List<Payment> { payment }, ct))[0];
    }

    /// <summary>
    /// Locks the withdrawal's row for the rest of the transaction, so two staff members acting on
    /// the same withdrawal at once cannot both pay it, or pay it and refund it.
    /// </summary>
    private async Task<Payment> LockPendingAsync(Guid paymentId, CancellationToken ct)
    {
        var payment = await _db.Payments
            .FromSqlRaw("""SELECT * FROM "Payments" WHERE "Id" = {0} FOR UPDATE""", paymentId)
            .FirstOrDefaultAsync(ct);

        if (payment is null || payment.PaymentType != PaymentType.Withdrawal)
        {
            throw new DomainException(ErrorCodes.NotFound, "Withdrawal not found.");
        }

        if (payment.Status is not (PaymentStatus.Pending or PaymentStatus.Processing))
        {
            throw new DomainException(
                ErrorCodes.WithdrawalNotPending, "This withdrawal has already been settled.");
        }

        return payment;
    }

    private async Task<List<AdminWithdrawalDto>> MapAsync(List<Payment> payments, CancellationToken ct)
    {
        var userIds = payments.Select(p => p.UserId).Distinct().ToList();

        var users = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.PhoneNumber, u.Email })
            .ToDictionaryAsync(u => u.Id, ct);

        // Withdrawals made before the IBAN was recorded on the payment carry it only in the
        // ledger entry's description, found through the shared idempotency key.
        var legacy = payments.Where(p => p.Metadata is null || !p.Metadata.StartsWith(IbanPrefix)).ToList();
        var legacyKeys = legacy.Where(p => p.IdempotencyKey != null).Select(p => p.IdempotencyKey!).ToList();
        var legacyUsers = legacy.Select(p => p.UserId).Distinct().ToList();

        var descriptions = legacyKeys.Count == 0
            ? new Dictionary<(Guid, string), string?>()
            : (await _db.WalletTransactions.AsNoTracking()
                .Where(t => t.TransactionType == WalletTransactionType.Withdrawal &&
                            legacyUsers.Contains(t.UserId) &&
                            t.IdempotencyKey != null && legacyKeys.Contains(t.IdempotencyKey))
                .Select(t => new { t.UserId, t.IdempotencyKey, t.Description })
                .ToListAsync(ct))
              .GroupBy(t => (t.UserId, t.IdempotencyKey!))
              .ToDictionary(g => g.Key, g => g.First().Description);

        return payments.Select(p =>
        {
            var user = users.GetValueOrDefault(p.UserId);

            string? iban = null;
            if (p.Metadata?.StartsWith(IbanPrefix) == true)
            {
                iban = p.Metadata[IbanPrefix.Length..];
            }
            else if (p.IdempotencyKey != null &&
                     descriptions.TryGetValue((p.UserId, p.IdempotencyKey), out var d) &&
                     d?.StartsWith(LegacyDescriptionPrefix) == true)
            {
                iban = d[LegacyDescriptionPrefix.Length..];
            }

            return new AdminWithdrawalDto(
                p.Id, p.UserId, user?.FullName ?? string.Empty, user?.PhoneNumber, user?.Email,
                p.Amount, p.Currency, iban, p.Status, p.CreatedAt, p.CompletedAt,
                p.Status == PaymentStatus.Completed ? p.ProviderTransactionId : null,
                p.FailureReason);
        }).ToList();
    }
}
