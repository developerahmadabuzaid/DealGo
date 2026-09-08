using AuctionPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuctionPlatform.Infrastructure.Persistence.Configurations;

public sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> b)
    {
        b.Property(u => u.FullName).HasMaxLength(256).IsRequired();
        b.Property(u => u.Language).HasMaxLength(8).IsRequired();

        b.HasIndex(u => u.IsActive);
        b.HasIndex(u => u.CreatedAt);
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("RefreshTokens");
        b.HasKey(t => t.Id);

        b.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
        b.Property(t => t.RevokedReason).HasMaxLength(256);
        b.Property(t => t.CreatedByIp).HasMaxLength(64);
        b.Property(t => t.UserAgent).HasMaxLength(512);

        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.UserId);
        b.HasIndex(t => t.ExpiresAt);

        b.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class UserDeviceTokenConfiguration : IEntityTypeConfiguration<UserDeviceToken>
{
    public void Configure(EntityTypeBuilder<UserDeviceToken> b)
    {
        b.ToTable("UserDeviceTokens");
        b.HasKey(t => t.Id);

        b.Property(t => t.Token).HasMaxLength(512).IsRequired();
        b.Property(t => t.Platform).HasMaxLength(32);

        b.HasIndex(t => t.Token).IsUnique();
        b.HasIndex(t => t.UserId);

        b.HasOne(t => t.User)
            .WithMany(u => u.DeviceTokens)
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("Notifications");
        b.HasKey(n => n.Id);

        b.Property(n => n.TitleKey).HasMaxLength(128).IsRequired();
        b.Property(n => n.BodyKey).HasMaxLength(128).IsRequired();
        b.Property(n => n.Title).HasMaxLength(256).IsRequired();
        b.Property(n => n.Body).HasMaxLength(2000).IsRequired();
        b.Property(n => n.Data).HasColumnType("jsonb");

        b.HasIndex(n => n.UserId);
        b.HasIndex(n => new { n.UserId, n.ReadAt });
        b.HasIndex(n => n.CreatedAt);

        b.HasOne(n => n.User)
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.HasKey(a => a.Id);

        b.Property(a => a.EntityType).HasMaxLength(128).IsRequired();
        b.Property(a => a.ActorEmail).HasMaxLength(256);
        b.Property(a => a.OldValue).HasColumnType("jsonb");
        b.Property(a => a.NewValue).HasColumnType("jsonb");
        b.Property(a => a.Reason).HasMaxLength(1024);
        b.Property(a => a.IpAddress).HasMaxLength(64);
        b.Property(a => a.UserAgent).HasMaxLength(512);

        b.HasIndex(a => a.CreatedAt);
        b.HasIndex(a => a.Action);
        b.HasIndex(a => new { a.EntityType, a.EntityId });
        b.HasIndex(a => a.ActorUserId);
    }
}

public sealed class OtpCodeConfiguration : IEntityTypeConfiguration<OtpCode>
{
    public void Configure(EntityTypeBuilder<OtpCode> b)
    {
        b.ToTable("OtpCodes");
        b.HasKey(o => o.Id);

        b.Property(o => o.Destination).HasMaxLength(256).IsRequired();
        b.Property(o => o.CodeHash).HasMaxLength(128).IsRequired();
        b.Property(o => o.OperationBinding).HasMaxLength(128);
        b.Property(o => o.InvalidatedReason).HasMaxLength(256);
        b.Property(o => o.IpAddress).HasMaxLength(64);
        b.Property(o => o.UserAgent).HasMaxLength(512);

        // Verification looks up the newest code for a destination and purpose.
        b.HasIndex(o => new { o.Destination, o.Purpose, o.CreatedAt });

        // Rate limiting counts recent requests per destination.
        b.HasIndex(o => new { o.Destination, o.CreatedAt });

        b.HasIndex(o => o.UserId);
        b.HasIndex(o => o.ExpiresAt);

        b.HasOne(o => o.User)
            .WithMany()
            .HasForeignKey(o => o.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
