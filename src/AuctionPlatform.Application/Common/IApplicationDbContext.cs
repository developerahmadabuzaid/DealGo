using AuctionPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuctionPlatform.Application.Common;

/// <summary>
/// The persistence surface the application layer is allowed to touch. Infrastructure supplies the
/// EF Core implementation; keeping this as an interface stops controllers reaching for the context.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<ApplicationUser> Users { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<UserDeviceToken> UserDeviceTokens { get; }
    DbSet<OtpCode> OtpCodes { get; }

    DbSet<Product> Products { get; }
    DbSet<Auction> Auctions { get; }
    DbSet<AuctionPrize> AuctionPrizes { get; }
    DbSet<AuctionParticipant> AuctionParticipants { get; }
    DbSet<Bid> Bids { get; }
    DbSet<Winner> Winners { get; }
    DbSet<PrizeClaim> PrizeClaims { get; }
    DbSet<AuctionEvent> AuctionEvents { get; }
    DbSet<AuctionEntryFeeRefund> AuctionEntryFeeRefunds { get; }

    DbSet<Wallet> Wallets { get; }
    DbSet<WalletTransaction> WalletTransactions { get; }
    DbSet<Payment> Payments { get; }
    DbSet<PaymentWebhookEvent> PaymentWebhookEvents { get; }

    DbSet<StoreCategory> StoreCategories { get; }
    DbSet<StoreProduct> StoreProducts { get; }
    DbSet<StoreProductImage> StoreProductImages { get; }
    DbSet<InventoryTransaction> InventoryTransactions { get; }
    DbSet<StoreOrder> StoreOrders { get; }
    DbSet<StoreOrderItem> StoreOrderItems { get; }
    DbSet<ShippingAddress> ShippingAddresses { get; }
    DbSet<StoreSettings> StoreSettings { get; }

    DbSet<Notification> Notifications { get; }
    DbSet<AuditLog> AuditLogs { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes raw SQL. Used by the bid engine and wallet operations for the row-level locking
    /// and conditional updates that EF Core cannot express.
    /// </summary>
    Task<int> ExecuteSqlAsync(string sql, object[] parameters, CancellationToken cancellationToken = default);
}
