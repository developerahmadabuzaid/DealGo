using AuctionPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuctionPlatform.Infrastructure.Persistence.Configurations;

public sealed class AuctionConfiguration : IEntityTypeConfiguration<Domain.Entities.Auction>
{
    public void Configure(EntityTypeBuilder<Domain.Entities.Auction> b)
    {
        b.ToTable("Auctions");
        b.HasKey(a => a.Id);

        b.Property(a => a.AuctionNumber).HasMaxLength(32).IsRequired();
        b.HasIndex(a => a.AuctionNumber).IsUnique();

        b.Property(a => a.Title).HasMaxLength(256).IsRequired();
        b.Property(a => a.Description).HasMaxLength(4000);
        b.Property(a => a.MainProductNameSnapshot).HasMaxLength(256);
        b.Property(a => a.MainImageSnapshot).HasMaxLength(1024);
        b.Property(a => a.CoverImageUrl).HasMaxLength(1024);
        // The front page asks for these on every visit, in this order.
        b.HasIndex(a => new { a.IsFeatured, a.FeaturedOrder });
        b.Property(a => a.Currency).HasMaxLength(3).IsRequired();

        // Optimistic concurrency on top of the explicit row locks the bid engine takes.
        b.Property(a => a.Version).IsRowVersion();

        b.HasOne(a => a.MainProduct)
            .WithMany()
            .HasForeignKey(a => a.MainProductId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(a => a.Status);
        b.HasIndex(a => a.ScheduledStartAt);
        b.HasIndex(a => a.FinishedAt);
        b.HasIndex(a => new { a.Status, a.ScheduledStartAt });
        b.HasIndex(a => a.CreatedAt);
    }
}

public sealed class AuctionPrizeConfiguration : IEntityTypeConfiguration<AuctionPrize>
{
    public void Configure(EntityTypeBuilder<AuctionPrize> b)
    {
        b.ToTable("AuctionPrizes");
        b.HasKey(p => p.Id);

        b.Property(p => p.ProductNameSnapshot).HasMaxLength(256).IsRequired();
        b.Property(p => p.ProductImageSnapshot).HasMaxLength(1024);
        b.Property(p => p.Description).HasMaxLength(2000);

        // Exactly one prize per rank on an auction.
        b.HasIndex(p => new { p.AuctionId, p.Rank }).IsUnique();

        b.HasOne(p => p.Auction)
            .WithMany(a => a.Prizes)
            .HasForeignKey(p => p.AuctionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(p => p.Product)
            .WithMany()
            .HasForeignKey(p => p.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AuctionParticipantConfiguration : IEntityTypeConfiguration<AuctionParticipant>
{
    public void Configure(EntityTypeBuilder<AuctionParticipant> b)
    {
        b.ToTable("AuctionParticipants");
        b.HasKey(p => p.Id);

        b.Property(p => p.Currency).HasMaxLength(3).IsRequired();
        b.Property(p => p.Version).IsRowVersion();

        // A user cannot join the same auction twice.
        b.HasIndex(p => new { p.AuctionId, p.UserId }).IsUnique();

        b.HasIndex(p => p.AuctionId);
        b.HasIndex(p => p.UserId);

        // Serves the ranking query directly: highest price first, earliest timestamp wins ties.
        b.HasIndex(p => new { p.AuctionId, p.HighestPrice, p.HighestPriceAt });

        b.HasOne(p => p.Auction)
            .WithMany(a => a.Participants)
            .HasForeignKey(p => p.AuctionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class BidConfiguration : IEntityTypeConfiguration<Bid>
{
    public void Configure(EntityTypeBuilder<Bid> b)
    {
        b.ToTable("Bids");
        b.HasKey(x => x.Id);

        b.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();

        // No two bids may share a sequence number on the same auction.
        b.HasIndex(x => new { x.AuctionId, x.SequenceNumber }).IsUnique();

        // A retried bid request resolves to the original bid rather than creating a second one.
        b.HasIndex(x => new { x.AuctionId, x.ParticipantId, x.IdempotencyKey }).IsUnique();

        b.HasIndex(x => x.AuctionId);
        b.HasIndex(x => new { x.AuctionId, x.ServerTimestamp });

        b.HasOne(x => x.Auction)
            .WithMany(a => a.Bids)
            .HasForeignKey(x => x.AuctionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.Participant)
            .WithMany(p => p.Bids)
            .HasForeignKey(x => x.ParticipantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WinnerConfiguration : IEntityTypeConfiguration<Winner>
{
    public void Configure(EntityTypeBuilder<Winner> b)
    {
        b.ToTable("Winners");
        b.HasKey(w => w.Id);

        b.HasIndex(w => new { w.AuctionId, w.Rank }).IsUnique();
        b.HasIndex(w => new { w.AuctionId, w.ParticipantId }).IsUnique();
        b.HasIndex(w => w.UserId);

        b.HasOne(w => w.Auction)
            .WithMany(a => a.Winners)
            .HasForeignKey(w => w.AuctionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(w => w.Participant)
            .WithMany()
            .HasForeignKey(w => w.ParticipantId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(w => w.Prize)
            .WithMany()
            .HasForeignKey(w => w.PrizeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PrizeClaimConfiguration : IEntityTypeConfiguration<PrizeClaim>
{
    public void Configure(EntityTypeBuilder<PrizeClaim> b)
    {
        b.ToTable("PrizeClaims");
        b.HasKey(c => c.Id);

        b.Property(c => c.Currency).HasMaxLength(3).IsRequired();
        b.Property(c => c.Version).IsRowVersion();

        b.HasIndex(c => c.UserId);
        b.HasIndex(c => c.Status);
        b.HasIndex(c => new { c.UserId, c.Status });

        b.HasOne(c => c.Prize)
            .WithMany()
            .HasForeignKey(c => c.PrizeId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(c => c.Winner)
            .WithMany()
            .HasForeignKey(c => c.WinnerId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);
    }
}

public sealed class AuctionEventConfiguration : IEntityTypeConfiguration<AuctionEvent>
{
    public void Configure(EntityTypeBuilder<AuctionEvent> b)
    {
        b.ToTable("AuctionEvents");
        b.HasKey(e => e.Id);

        b.Property(e => e.OldValue).HasMaxLength(2000);
        b.Property(e => e.NewValue).HasMaxLength(2000);
        b.Property(e => e.Metadata).HasMaxLength(4000);
        b.Property(e => e.IpAddress).HasMaxLength(64);
        b.Property(e => e.UserAgent).HasMaxLength(512);

        b.HasIndex(e => new { e.AuctionId, e.Timestamp });
        b.HasIndex(e => e.EventType);

        b.HasOne(e => e.Auction)
            .WithMany()
            .HasForeignKey(e => e.AuctionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AuctionEntryFeeRefundConfiguration : IEntityTypeConfiguration<AuctionEntryFeeRefund>
{
    public void Configure(EntityTypeBuilder<AuctionEntryFeeRefund> b)
    {
        b.ToTable("AuctionEntryFeeRefunds");
        b.HasKey(r => r.Id);

        b.Property(r => r.Currency).HasMaxLength(3).IsRequired();

        // The exactly-once guarantee for entry fee refunds.
        b.HasIndex(r => new { r.AuctionId, r.ParticipantId }).IsUnique();

        b.HasIndex(r => r.UserId);
        b.HasIndex(r => r.Status);

        b.HasOne(r => r.Auction)
            .WithMany()
            .HasForeignKey(r => r.AuctionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(r => r.Participant)
            .WithMany()
            .HasForeignKey(r => r.ParticipantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("Products");
        b.HasKey(p => p.Id);

        b.Property(p => p.Name).HasMaxLength(256).IsRequired();
        b.Property(p => p.Description).HasMaxLength(4000);
        b.Property(p => p.Currency).HasMaxLength(3).IsRequired();
        b.Property(p => p.PrimaryImageUrl).HasMaxLength(1024);

        // Stored as jsonb. The value comparer lets EF detect in-place edits to the list.
        b.Property(p => p.ImageUrls)
            .HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (a, c) => a != null && c != null && a.SequenceEqual(c),
                    v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                    v => v.ToList()));

        b.HasIndex(p => p.IsActive);
    }
}
