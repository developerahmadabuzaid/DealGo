using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Application.Identity;

public sealed record DeleteAccountRequest(string Code);

/// <summary>
/// Lets a user delete their own account, confirmed with a one-time code.
///
/// The account row itself stays, because the wallet ledger and payment records are kept for the
/// financial record and the database refuses to delete them. Everything that identifies the person
/// is removed instead: name, email, phone, password, devices and notifications. The account is
/// disabled and every session ends. Signing in again with the same number starts a new account.
/// </summary>
public interface IAccountDeletionService
{
    /// <summary>
    /// Checks the account can be deleted now, then sends the confirmation code to the user's phone
    /// (or email when there is no phone).
    /// </summary>
    Task<OtpChallengeDto> RequestAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Verifies the code and deletes the account.</summary>
    Task DeleteAsync(Guid userId, DeleteAccountRequest request, CancellationToken ct = default);
}

public sealed class AccountDeletionService : IAccountDeletionService
{
    // Auctions the user is still in: anything between registration and the winners being declared.
    private static readonly AuctionStatus[] OpenAuctionStatuses =
    {
        AuctionStatus.Scheduled, AuctionStatus.RegistrationOpen, AuctionStatus.WaitingForParticipants,
        AuctionStatus.Ready, AuctionStatus.WaitingForStart, AuctionStatus.Live, AuctionStatus.FinalPhase,
        AuctionStatus.Finished, AuctionStatus.Paused
    };

    // A device still to be paid for, or paid for and not yet handed over.
    private static readonly PrizeClaimStatus[] OpenClaimStatuses =
    {
        PrizeClaimStatus.Pending, PrizeClaimStatus.PaymentRequired, PrizeClaimStatus.PaymentProcessing,
        PrizeClaimStatus.Paid, PrizeClaimStatus.Claimed
    };

    private static readonly StoreOrderStatus[] OpenOrderStatuses =
    {
        StoreOrderStatus.Pending, StoreOrderStatus.Paid, StoreOrderStatus.Processing, StoreOrderStatus.Shipped
    };

    private readonly IApplicationDbContext _db;
    private readonly IOtpService _otp;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;

    public AccountDeletionService(
        IApplicationDbContext db, IOtpService otp, IAuditService audit, IDateTimeProvider clock)
    {
        _db = db;
        _otp = otp;
        _audit = audit;
        _clock = clock;
    }

    public async Task<OtpChallengeDto> RequestAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await FindActiveAsync(userId, ct);

        // Checked before a code is sent, so the user is not asked to confirm something that will
        // be refused.
        await EnsureDeletableAsync(userId, ct);

        return await _otp.RequestAsync(new OtpRequest
        {
            Destination = CodeDestination(user),
            Purpose = OtpPurpose.DeleteAccount,
            UserId = userId,
            OperationBinding = Binding(userId),
            Language = user.Language
        }, ct);
    }

    public async Task DeleteAsync(Guid userId, DeleteAccountRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new DomainException(ErrorCodes.OtpRequired, "A confirmation code is required.");
        }

        var user = await FindActiveAsync(userId, ct);

        await _otp.VerifyAsync(new OtpVerification
        {
            Destination = CodeDestination(user),
            Purpose = OtpPurpose.DeleteAccount,
            Code = request.Code,
            UserId = userId,
            OperationBinding = Binding(userId)
        }, ct);

        await using var tx = await _db.BeginTransactionAsync(ct);

        // Checked again inside the transaction: something may have started after the code was sent.
        await EnsureDeletableAsync(userId, ct);

        var now = _clock.UtcNow;
        var placeholder = $"deleted-{userId:N}";

        user.FullName = string.Empty;
        user.Email = null;
        user.NormalizedEmail = null;
        user.EmailConfirmed = false;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;
        user.UserName = placeholder;
        user.NormalizedUserName = placeholder.ToUpperInvariant();
        user.PasswordHash = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.IsActive = false;
        user.NotificationsEnabled = false;
        user.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);

        await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.RevokedReason, "Account deleted"), ct);

        await _db.UserDeviceTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.Notifications.Where(n => n.UserId == userId).ExecuteDeleteAsync(ct);

        // Addresses on past orders stay with those orders; the rest go.
        await _db.ShippingAddresses
            .Where(a => a.UserId == userId && !_db.StoreOrders.Any(o => o.ShippingAddressId == a.Id))
            .ExecuteDeleteAsync(ct);

        await _audit.LogAsync(
            AuditActionType.AccountDeleted, nameof(ApplicationUser), userId,
            reason: "Deleted by the user, confirmed with a one-time code", ct: ct);

        await tx.CommitAsync(ct);
    }

    private async Task<ApplicationUser> FindActiveAsync(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");

        if (!user.IsActive)
        {
            throw new DomainException(ErrorCodes.AccountDisabled, "This account has been disabled.");
        }

        return user;
    }

    /// <summary>
    /// Refuses while deleting would lose the user's money or leave something half done: a balance
    /// they can still withdraw, an auction they are in, a device they won, an order on its way, or
    /// a payment still being processed. Shopping credit cannot be withdrawn, so it does not block.
    /// </summary>
    private async Task EnsureDeletableAsync(Guid userId, CancellationToken ct)
    {
        var balance = await _db.Wallets
            .Where(w => w.UserId == userId && w.WalletType == WalletType.Main)
            .SumAsync(w => (decimal?)w.Balance, ct) ?? 0m;

        if (balance > 0)
        {
            throw new DomainException(
                ErrorCodes.AccountHasBalance, "Withdraw the main wallet balance before deleting the account.");
        }

        var open =
            await _db.AuctionParticipants.AnyAsync(p => p.UserId == userId &&
                OpenAuctionStatuses.Contains(p.Auction!.Status), ct) ||
            await _db.PrizeClaims.AnyAsync(c => c.UserId == userId && OpenClaimStatuses.Contains(c.Status), ct) ||
            await _db.StoreOrders.AnyAsync(o => o.UserId == userId && OpenOrderStatuses.Contains(o.Status), ct) ||
            await _db.Payments.AnyAsync(p => p.UserId == userId &&
                (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Processing), ct);

        if (open)
        {
            throw new DomainException(
                ErrorCodes.AccountHasOpenActivity,
                "The account has an auction, device, order or payment still in progress.");
        }
    }

    private static string CodeDestination(ApplicationUser user)
    {
        if (SaudiPhone.TryNormalise(user.PhoneNumber, out var phone)) return phone;

        return user.Email ?? throw new DomainException(
            ErrorCodes.OtpDestinationInvalid, "The account has no phone number or email to send a code to.");
    }

    private static string Binding(Guid userId) => $"delete-account:{userId:N}";
}
