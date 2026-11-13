using AuctionPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuctionPlatform.Infrastructure.Persistence.Configurations;

public sealed class NotificationMessageSettingConfiguration : IEntityTypeConfiguration<NotificationMessageSetting>
{
    public void Configure(EntityTypeBuilder<NotificationMessageSetting> b)
    {
        b.ToTable("NotificationMessageSettings");
        b.HasKey(s => s.Id);
        b.Property(s => s.Code).HasMaxLength(64).IsRequired();
        b.HasIndex(s => s.Code).IsUnique();
        b.Property(s => s.TitleAr).HasMaxLength(100);
        b.Property(s => s.TitleEn).HasMaxLength(100);
        b.Property(s => s.BodyAr).HasMaxLength(1000);
        b.Property(s => s.BodyEn).HasMaxLength(1000);
    }
}

public sealed class ScheduledNotificationConfiguration : IEntityTypeConfiguration<ScheduledNotification>
{
    public void Configure(EntityTypeBuilder<ScheduledNotification> b)
    {
        b.ToTable("ScheduledNotifications");
        b.HasKey(s => s.Id);
        b.Property(s => s.Title).HasMaxLength(100).IsRequired();
        b.Property(s => s.Body).HasMaxLength(1000).IsRequired();
        b.Property(s => s.Error).HasMaxLength(500);
        b.Property(s => s.Version).IsRowVersion();

        // The worker's question every half minute: what is due now.
        b.HasIndex(s => new { s.Status, s.SendAt });
    }
}
