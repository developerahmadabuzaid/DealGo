using AuctionPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuctionPlatform.Infrastructure.Persistence.Configurations;

public sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> b)
    {
        b.ToTable("Wallets", t => t.HasCheckConstraint("CK_Wallets_NonNegative", "\"Balance\" >= 0"));
        b.HasKey(w => w.Id);

        b.Property(w => w.Currency).HasMaxLength(3).IsRequired();
        b.Property(w => w.Version).IsRowVersion();

        // Exactly one Main and one Shopping wallet per user.
        b.HasIndex(w => new { w.UserId, w.WalletType }).IsUnique();
        b.HasIndex(w => w.UserId);

        b.HasOne(w => w.User)
            .WithMany(u => u.Wallets)
            .HasForeignKey(w => w.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WalletTransactionConfiguration : IEntityTypeConfiguration<WalletTransaction>
{
    public void Configure(EntityTypeBuilder<WalletTransaction> b)
    {
        b.ToTable("WalletTransactions");
        b.HasKey(t => t.Id);

        b.Property(t => t.Currency).HasMaxLength(3).IsRequired();
        b.Property(t => t.Description).HasMaxLength(512);
        b.Property(t => t.ReferenceType).HasMaxLength(64);
        b.Property(t => t.Metadata).HasMaxLength(4000);
        b.Property(t => t.IdempotencyKey).HasMaxLength(160);

        // Makes a retried financial command return the original ledger entry instead of moving
        // money a second time.
        b.HasIndex(t => new { t.UserId, t.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");

        b.HasIndex(t => t.UserId);
        b.HasIndex(t => new { t.UserId, t.WalletType, t.CreatedAt });
        b.HasIndex(t => t.ReferenceId);
        b.HasIndex(t => t.CreatedAt);

        b.HasOne(t => t.Wallet)
            .WithMany(w => w.Transactions)
            .HasForeignKey(t => t.WalletId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("Payments");
        b.HasKey(p => p.Id);

        b.Property(p => p.Currency).HasMaxLength(3).IsRequired();
        b.Property(p => p.Provider).HasMaxLength(64).IsRequired();
        b.Property(p => p.ProviderTransactionId).HasMaxLength(128);
        b.Property(p => p.IdempotencyKey).HasMaxLength(128);
        b.Property(p => p.FailureReason).HasMaxLength(1024);
        b.Property(p => p.Metadata).HasMaxLength(4000);

        // A redelivered provider webhook can never create a second payment.
        b.HasIndex(p => new { p.Provider, p.ProviderTransactionId })
            .IsUnique()
            .HasFilter("\"ProviderTransactionId\" IS NOT NULL");

        b.HasIndex(p => new { p.UserId, p.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");

        // A gateway order settles exactly one payment.
        b.HasIndex(p => new { p.Provider, p.ProviderOrderId })
            .IsUnique()
            .HasFilter("\"ProviderOrderId\" IS NOT NULL");

        b.HasIndex(p => p.UserId);
        b.HasIndex(p => p.Status);
        b.HasIndex(p => p.CreatedAt);

        b.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PaymentWebhookEventConfiguration : IEntityTypeConfiguration<PaymentWebhookEvent>
{
    public void Configure(EntityTypeBuilder<PaymentWebhookEvent> b)
    {
        b.ToTable("PaymentWebhookEvents");
        b.HasKey(e => e.Id);

        b.Property(e => e.Provider).HasMaxLength(64).IsRequired();
        b.Property(e => e.ProviderEventId).HasMaxLength(128).IsRequired();
        b.Property(e => e.ProviderTransactionId).HasMaxLength(128);
        b.Property(e => e.Signature).HasMaxLength(512);
        b.Property(e => e.ProcessingError).HasMaxLength(2000);

        // Redelivery of the same event is detected here.
        b.HasIndex(e => new { e.Provider, e.ProviderEventId }).IsUnique();
        b.HasIndex(e => e.Processed);
    }
}
