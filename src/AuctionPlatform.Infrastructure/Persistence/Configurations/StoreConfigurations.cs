using AuctionPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuctionPlatform.Infrastructure.Persistence.Configurations;

public sealed class StoreCategoryConfiguration : IEntityTypeConfiguration<StoreCategory>
{
    public void Configure(EntityTypeBuilder<StoreCategory> b)
    {
        b.ToTable("StoreCategories");
        b.HasKey(c => c.Id);

        b.Property(c => c.Name).HasMaxLength(128).IsRequired();
        b.Property(c => c.Slug).HasMaxLength(160).IsRequired();
        b.Property(c => c.Description).HasMaxLength(2000);
        b.Property(c => c.ImageUrl).HasMaxLength(1024);

        b.HasIndex(c => c.Slug).IsUnique();
        b.HasIndex(c => c.Status);
        b.HasIndex(c => c.DisplayOrder);
        b.HasIndex(c => c.ParentId);

        // A main department with sub-departments cannot be deleted out from under them.
        b.HasOne(c => c.Parent)
            .WithMany(c => c.Children)
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class StoreProductConfiguration : IEntityTypeConfiguration<StoreProduct>
{
    public void Configure(EntityTypeBuilder<StoreProduct> b)
    {
        b.ToTable("StoreProducts", t => t.HasCheckConstraint("CK_StoreProducts_Stock", "\"StockQuantity\" >= 0"));
        b.HasKey(p => p.Id);

        b.Property(p => p.Name).HasMaxLength(256).IsRequired();
        b.Property(p => p.Slug).HasMaxLength(300).IsRequired();
        b.Property(p => p.Description).HasMaxLength(8000);
        b.Property(p => p.Sku).HasMaxLength(64).IsRequired();
        b.Property(p => p.Currency).HasMaxLength(3).IsRequired();
        b.Property(p => p.Version).IsRowVersion();

        b.HasIndex(p => p.Sku).IsUnique();
        b.HasIndex(p => p.Slug);
        b.HasIndex(p => p.Status);
        b.HasIndex(p => p.CategoryId);
        b.HasIndex(p => new { p.Status, p.IsFeatured });
        b.HasIndex(p => new { p.Status, p.CompareAtPrice });
        b.HasIndex(p => p.CreatedAt);

        b.HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class StoreProductImageConfiguration : IEntityTypeConfiguration<StoreProductImage>
{
    public void Configure(EntityTypeBuilder<StoreProductImage> b)
    {
        b.ToTable("StoreProductImages");
        b.HasKey(i => i.Id);

        b.Property(i => i.Url).HasMaxLength(1024).IsRequired();
        b.Property(i => i.ThumbnailUrl).HasMaxLength(1024);

        b.HasIndex(i => new { i.ProductId, i.DisplayOrder });

        b.HasOne(i => i.Product)
            .WithMany(p => p.Images)
            .HasForeignKey(i => i.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class InventoryTransactionConfiguration : IEntityTypeConfiguration<InventoryTransaction>
{
    public void Configure(EntityTypeBuilder<InventoryTransaction> b)
    {
        b.ToTable("InventoryTransactions");
        b.HasKey(t => t.Id);

        b.Property(t => t.Reason).HasMaxLength(512);
        b.Property(t => t.ReferenceType).HasMaxLength(64);

        b.HasIndex(t => new { t.ProductId, t.CreatedAt });
        b.HasIndex(t => t.ReferenceId);

        b.HasOne(t => t.Product)
            .WithMany()
            .HasForeignKey(t => t.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class StoreOrderConfiguration : IEntityTypeConfiguration<StoreOrder>
{
    public void Configure(EntityTypeBuilder<StoreOrder> b)
    {
        b.ToTable("StoreOrders");
        b.HasKey(o => o.Id);

        b.Property(o => o.OrderNumber).HasMaxLength(32).IsRequired();
        b.Property(o => o.Currency).HasMaxLength(3).IsRequired();
        b.Property(o => o.IdempotencyKey).HasMaxLength(128);
        b.Property(o => o.TrackingNumber).HasMaxLength(128);
        b.Property(o => o.CancellationReason).HasMaxLength(1024);
        b.Property(o => o.Version).IsRowVersion();

        b.HasIndex(o => o.OrderNumber).IsUnique();

        // A retried checkout resolves to the existing order.
        b.HasIndex(o => new { o.UserId, o.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");

        b.HasIndex(o => o.UserId);
        b.HasIndex(o => o.Status);
        b.HasIndex(o => o.CreatedAt);

        b.HasOne(o => o.User)
            .WithMany()
            .HasForeignKey(o => o.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(o => o.ShippingAddress)
            .WithMany()
            .HasForeignKey(o => o.ShippingAddressId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class StoreOrderItemConfiguration : IEntityTypeConfiguration<StoreOrderItem>
{
    public void Configure(EntityTypeBuilder<StoreOrderItem> b)
    {
        b.ToTable("StoreOrderItems");
        b.HasKey(i => i.Id);

        b.Property(i => i.ProductNameSnapshot).HasMaxLength(256).IsRequired();
        b.Property(i => i.ProductSkuSnapshot).HasMaxLength(64);
        b.Property(i => i.ProductImageSnapshot).HasMaxLength(1024);

        b.HasIndex(i => i.OrderId);
        b.HasIndex(i => i.ProductId);

        b.HasOne(i => i.Order)
            .WithMany(o => o.Items)
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: a product referenced by an order is archived, never deleted.
        b.HasOne(i => i.Product)
            .WithMany()
            .HasForeignKey(i => i.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ShippingAddressConfiguration : IEntityTypeConfiguration<ShippingAddress>
{
    public void Configure(EntityTypeBuilder<ShippingAddress> b)
    {
        b.ToTable("ShippingAddresses");
        b.HasKey(a => a.Id);

        b.Property(a => a.FullName).HasMaxLength(256).IsRequired();
        b.Property(a => a.Phone).HasMaxLength(32).IsRequired();
        b.Property(a => a.Country).HasMaxLength(128).IsRequired();
        b.Property(a => a.City).HasMaxLength(128).IsRequired();
        b.Property(a => a.Area).HasMaxLength(128);
        b.Property(a => a.AddressLine1).HasMaxLength(512).IsRequired();
        b.Property(a => a.AddressLine2).HasMaxLength(512);
        b.Property(a => a.PostalCode).HasMaxLength(32);

        b.HasIndex(a => a.UserId);

        b.HasOne(a => a.User)
            .WithMany(u => u.Addresses)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class StoreSettingsConfiguration : IEntityTypeConfiguration<StoreSettings>
{
    public void Configure(EntityTypeBuilder<StoreSettings> b)
    {
        b.ToTable("StoreSettings");
        b.HasKey(s => s.Id);
        b.Property(s => s.DefaultCurrency).HasMaxLength(3).IsRequired();
        b.Property(s => s.RefundPolicy).HasMaxLength(8000);
    }
}
