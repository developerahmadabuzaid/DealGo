using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuctionPlatform.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context. Identity tables live alongside the domain tables so that a user and
/// their wallets can be created in one transaction.
/// </summary>
public class ApplicationDbContext
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UserDeviceToken> UserDeviceTokens => Set<UserDeviceToken>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Domain.Entities.Auction> Auctions => Set<Domain.Entities.Auction>();
    public DbSet<AuctionPrize> AuctionPrizes => Set<AuctionPrize>();
    public DbSet<AuctionParticipant> AuctionParticipants => Set<AuctionParticipant>();
    public DbSet<Bid> Bids => Set<Bid>();
    public DbSet<Winner> Winners => Set<Winner>();
    public DbSet<PrizeClaim> PrizeClaims => Set<PrizeClaim>();
    public DbSet<AuctionEvent> AuctionEvents => Set<AuctionEvent>();
    public DbSet<AuctionEntryFeeRefund> AuctionEntryFeeRefunds => Set<AuctionEntryFeeRefund>();

    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentWebhookEvent> PaymentWebhookEvents => Set<PaymentWebhookEvent>();

    public DbSet<StoreCategory> StoreCategories => Set<StoreCategory>();
    public DbSet<StoreProduct> StoreProducts => Set<StoreProduct>();
    public DbSet<StoreProductImage> StoreProductImages => Set<StoreProductImage>();
    public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();
    public DbSet<StoreOrder> StoreOrders => Set<StoreOrder>();
    public DbSet<StoreOrderItem> StoreOrderItems => Set<StoreOrderItem>();
    public DbSet<ShippingAddress> ShippingAddresses => Set<ShippingAddress>();
    public DbSet<StoreSettings> StoreSettings => Set<StoreSettings>();

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<SiteContent> SiteContent => Set<SiteContent>();
    public DbSet<SiteCard> SiteCards => Set<SiteCard>();
    public DbSet<SiteTestimonial> SiteTestimonials => Set<SiteTestimonial>();
    public DbSet<SiteFaq> SiteFaqs => Set<SiteFaq>();

    public DbSet<AppAd> AppAds => Set<AppAd>();

    DbSet<ApplicationUser> IApplicationDbContext.Users => Set<ApplicationUser>();

    /// <summary>
    /// Starts a transaction, or joins the one already in flight.
    ///
    /// When a service that manages its own transaction is called from inside another one, it gets a
    /// nested handle whose Commit and Dispose do nothing: the outermost caller owns the outcome.
    /// Without this, an inner `await using` would commit or dispose its caller's transaction.
    /// </summary>
    public async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var ambient = Database.CurrentTransaction;

        return ambient is not null
            ? new NestedTransaction(ambient)
            : await Database.BeginTransactionAsync(cancellationToken);
    }

    /// <summary>
    /// A pass-through handle for an already-open transaction. Commit and Dispose are deliberately
    /// no-ops; the transaction ends where it began. An exception still propagates to the owner,
    /// which is what actually rolls the work back.
    /// </summary>
    private sealed class NestedTransaction : IDbContextTransaction
    {
        private readonly IDbContextTransaction _inner;

        public NestedTransaction(IDbContextTransaction inner) => _inner = inner;

        public Guid TransactionId => _inner.TransactionId;

        public void Commit() { }
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Rollback() => _inner.Rollback();
        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            _inner.RollbackAsync(cancellationToken);

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public Task<int> ExecuteSqlAsync(string sql, object[] parameters, CancellationToken cancellationToken = default) =>
        Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // Database sequences back the human-friendly AUC-/ORD- numbers so they stay unique and
        // gap-tolerant across API instances.
        builder.HasSequence<long>("AuctionNumberSeq").StartsAt(1).IncrementsBy(1);
        builder.HasSequence<long>("OrderNumberSeq").StartsAt(1).IncrementsBy(1);

        // Money is numeric(18,2) everywhere. Floating point is never used for currency amounts.
        foreach (var property in builder.Model.GetEntityTypes()
                     .SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
        {
            property.SetColumnType("numeric(18,2)");
        }

        // Enums are stored as ints for compact, stable indexes.
        foreach (var property in builder.Model.GetEntityTypes()
                     .SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType.IsEnum ||
                                 (Nullable.GetUnderlyingType(p.ClrType)?.IsEnum ?? false)))
        {
            property.SetProviderClrType(typeof(int));
        }
    }
}
