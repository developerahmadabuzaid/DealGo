using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionPlatform.Application.Prizes;

public sealed record PrizeClaimDto(
    Guid Id,
    Guid AuctionId,
    string AuctionTitle,
    string AuctionNumber,
    int Rank,
    Guid PrizeId,
    string PrizeName,
    string? PrizeImageUrl,
    decimal PaymentAmount,
    string Currency,
    PrizeClaimStatus Status,
    DateTime CreatedAt,
    DateTime? PaymentDeadlineAt,
    DateTime? PaidAt,
    DateTime? ClaimedAt,
    DateTime? FulfilledAt,
    // The prize's full price, before the auction discount that gives PaymentAmount.
    decimal PrizeOriginalPrice,
    string? PrizeDescription);

public sealed record MyWinDto(
    Guid AuctionId,
    string AuctionTitle,
    string AuctionNumber,
    int Rank,
    decimal HighestPrice,
    string PrizeName,
    string? PrizeImageUrl,
    decimal PrizeValue,
    string Currency,
    Guid? PrizeClaimId,
    PrizeClaimStatus? ClaimStatus,
    DateTime RankedAt);

public sealed record PayPrizeRequest(string IdempotencyKey, Guid? ShippingAddressId);

public interface IPrizeClaimService
{
    Task<PagedResult<MyWinDto>> GetMyWinsAsync(Guid userId, PagedQuery query, CancellationToken ct = default);
    Task<PagedResult<PrizeClaimDto>> GetMyClaimsAsync(Guid userId, PagedQuery query, CancellationToken ct = default);
    Task<PrizeClaimDto> GetClaimAsync(Guid userId, Guid claimId, CancellationToken ct = default);

    /// <summary>Pays the prize value from the Main wallet. Shopping credit can never be used here.</summary>
    Task<PrizeClaimDto> PayAsync(Guid userId, Guid claimId, PayPrizeRequest request, CancellationToken ct = default);

    Task<PrizeClaimDto> ClaimAsync(Guid userId, Guid claimId, Guid? shippingAddressId, CancellationToken ct = default);
}

public sealed class PrizeClaimService : IPrizeClaimService
{
    private readonly IApplicationDbContext _db;
    private readonly IWalletService _wallets;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<PrizeClaimService> _logger;

    public PrizeClaimService(
        IApplicationDbContext db,
        IWalletService wallets,
        IDateTimeProvider clock,
        ILogger<PrizeClaimService> logger)
    {
        _db = db;
        _wallets = wallets;
        _clock = clock;
        _logger = logger;
    }

    public async Task<PagedResult<MyWinDto>> GetMyWinsAsync(
        Guid userId, PagedQuery query, CancellationToken ct = default)
    {
        var q = _db.Winners.AsNoTracking().Where(w => w.UserId == userId);
        var total = await q.LongCountAsync(ct);

        var items = await q
            .OrderByDescending(w => w.RankedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(w => new MyWinDto(
                w.AuctionId,
                w.Auction != null ? w.Auction.Title : string.Empty,
                w.Auction != null ? w.Auction.AuctionNumber : string.Empty,
                w.Rank,
                w.HighestPrice,
                w.Prize != null ? w.Prize.ProductNameSnapshot : string.Empty,
                w.Prize != null ? w.Prize.ProductImageSnapshot : null,
                w.Prize != null ? w.Prize.OriginalPrice : 0m,
                w.Auction != null ? w.Auction.Currency : Money.DefaultCurrency,
                w.PrizeClaimId,
                _db.PrizeClaims.Where(c => c.WinnerId == w.Id)
                    .Select(c => (PrizeClaimStatus?)c.Status).FirstOrDefault(),
                w.RankedAt))
            .ToListAsync(ct);

        return PagedResult<MyWinDto>.Create(items, query.Page, query.PageSize, total);
    }

    public async Task<PagedResult<PrizeClaimDto>> GetMyClaimsAsync(
        Guid userId, PagedQuery query, CancellationToken ct = default)
    {
        var q = _db.PrizeClaims.AsNoTracking().Where(c => c.UserId == userId);
        var total = await q.LongCountAsync(ct);

        var items = await q
            .OrderByDescending(c => c.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(Projection)
            .ToListAsync(ct);

        return PagedResult<PrizeClaimDto>.Create(items, query.Page, query.PageSize, total);
    }

    private static System.Linq.Expressions.Expression<Func<PrizeClaim, PrizeClaimDto>> Projection =>
        c => new PrizeClaimDto(
            c.Id, c.AuctionId,
            c.Prize != null && c.Prize.Auction != null ? c.Prize.Auction.Title : string.Empty,
            c.Prize != null && c.Prize.Auction != null ? c.Prize.Auction.AuctionNumber : string.Empty,
            c.Prize != null ? c.Prize.Rank : 0,
            c.PrizeId,
            c.Prize != null ? c.Prize.ProductNameSnapshot : string.Empty,
            c.Prize != null ? c.Prize.ProductImageSnapshot : null,
            c.PaymentAmount, c.Currency, c.Status,
            c.CreatedAt, c.PaymentDeadlineAt, c.PaidAt, c.ClaimedAt, c.FulfilledAt,
            c.Prize != null ? c.Prize.OriginalPrice : c.PaymentAmount,
            c.Prize != null ? c.Prize.Description : null);

    public async Task<PrizeClaimDto> GetClaimAsync(Guid userId, Guid claimId, CancellationToken ct = default)
    {
        var claim = await _db.PrizeClaims
            .AsNoTracking()
            .Where(c => c.Id == claimId && c.UserId == userId)
            .Select(Projection)
            .FirstOrDefaultAsync(ct);

        return claim ?? throw new DomainException(ErrorCodes.PrizeClaimNotFound, "Prize claim not found.");
    }

    public async Task<PrizeClaimDto> PayAsync(
        Guid userId, Guid claimId, PayPrizeRequest request, CancellationToken ct = default)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);

        var claim = await _db.PrizeClaims
            .FromSqlRaw("""SELECT *, xmin FROM "PrizeClaims" WHERE "Id" = {0} FOR UPDATE""", claimId)
            .FirstOrDefaultAsync(ct)
            ?? throw new DomainException(ErrorCodes.PrizeClaimNotFound, "Prize claim not found.");

        if (claim.UserId != userId)
        {
            throw new DomainException(ErrorCodes.Forbidden, "This prize claim does not belong to you.");
        }

        if (claim.Status is PrizeClaimStatus.Paid or PrizeClaimStatus.Claimed or PrizeClaimStatus.Fulfilled)
        {
            await tx.CommitAsync(ct);
            return await GetClaimAsync(userId, claimId, ct);
        }

        if (claim.Status is not (PrizeClaimStatus.PaymentRequired or PrizeClaimStatus.Pending))
        {
            throw new DomainException(
                ErrorCodes.PrizeClaimNotPayable, $"A claim in status {claim.Status} cannot be paid.");
        }

        var now = _clock.UtcNow;

        // Prize payments come from the Main wallet only; store credit is not cash.
        var payment = await _wallets.DebitAsync(new WalletMovement
        {
            UserId = userId,
            WalletType = WalletType.Main,
            Amount = claim.PaymentAmount,
            Currency = claim.Currency,
            TransactionType = WalletTransactionType.PrizePayment,
            ReferenceType = nameof(PrizeClaim),
            ReferenceId = claim.Id,
            Description = "Prize payment",
            IdempotencyKey = $"prize:{claim.Id}:{request.IdempotencyKey}"
        }, ct);

        var paymentRecord = new Payment
        {
            UserId = userId,
            AuctionId = claim.AuctionId,
            PrizeClaimId = claim.Id,
            PaymentType = PaymentType.PrizePayment,
            Amount = claim.PaymentAmount,
            Currency = claim.Currency,
            Provider = "WALLET",
            Status = PaymentStatus.Completed,
            IdempotencyKey = request.IdempotencyKey,
            CreatedAt = now,
            CompletedAt = now
        };
        _db.Payments.Add(paymentRecord);

        claim.Status = PrizeClaimStatus.Paid;
        claim.PaidAt = now;
        claim.PaymentId = paymentRecord.Id;
        if (request.ShippingAddressId.HasValue) claim.ShippingAddressId = request.ShippingAddressId;

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Prize claim {ClaimId} paid by {UserId} ({Amount} {Currency}); wallet now {Balance}.",
            claimId, userId, claim.PaymentAmount, claim.Currency, payment.BalanceAfter);

        return await GetClaimAsync(userId, claimId, ct);
    }

    public async Task<PrizeClaimDto> ClaimAsync(
        Guid userId, Guid claimId, Guid? shippingAddressId, CancellationToken ct = default)
    {
        var claim = await _db.PrizeClaims.FirstOrDefaultAsync(c => c.Id == claimId, ct)
            ?? throw new DomainException(ErrorCodes.PrizeClaimNotFound, "Prize claim not found.");

        if (claim.UserId != userId)
        {
            throw new DomainException(ErrorCodes.Forbidden, "This prize claim does not belong to you.");
        }

        if (claim.Status != PrizeClaimStatus.Paid)
        {
            throw new DomainException(
                ErrorCodes.PrizeClaimNotClaimable, "The prize must be paid for before it can be claimed.");
        }

        claim.Status = PrizeClaimStatus.Claimed;
        claim.ClaimedAt = _clock.UtcNow;
        if (shippingAddressId.HasValue) claim.ShippingAddressId = shippingAddressId;

        await _db.SaveChangesAsync(ct);

        return await GetClaimAsync(userId, claimId, ct);
    }
}
