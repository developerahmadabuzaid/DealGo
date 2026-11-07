using AuctionPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuctionPlatform.Infrastructure.Persistence.Configurations;

public sealed class M5aznShipmentConfiguration : IEntityTypeConfiguration<M5aznShipment>
{
    public void Configure(EntityTypeBuilder<M5aznShipment> b)
    {
        b.ToTable("M5aznShipments");
        b.HasKey(s => s.Id);

        // The number M5azn knows the order by. Starts high so it never looks like a test value.
        b.Property(s => s.Reference)
            .UseIdentityByDefaultColumn()
            .HasIdentityOptions(startValue: 100001);
        b.HasIndex(s => s.Reference).IsUnique();

        b.Property(s => s.RemoteOrderId).HasMaxLength(64);
        b.Property(s => s.RemoteStatus).HasMaxLength(128);
        b.Property(s => s.TrackingNumber).HasMaxLength(128);
        b.Property(s => s.TrackingUrl).HasMaxLength(1024);
        b.Property(s => s.Carrier).HasMaxLength(128);
        b.Property(s => s.LastError).HasMaxLength(2000);
        b.Property(s => s.RequestJson).HasColumnType("text");
        b.Property(s => s.Version).IsRowVersion();
        b.Ignore(s => s.IsInFlight);

        // A store order is never sent twice.
        b.HasIndex(s => new { s.SourceType, s.SourceId }).IsUnique();
        b.HasIndex(s => new { s.Status, s.NextAttemptAt });
        b.HasIndex(s => s.UserId);
    }
}

public sealed class M5aznSettingsConfiguration : IEntityTypeConfiguration<M5aznSettings>
{
    public void Configure(EntityTypeBuilder<M5aznSettings> b)
    {
        b.ToTable("M5aznSettings");
        b.HasKey(s => s.Id);
        b.Property(s => s.Email).HasMaxLength(256);
        b.Property(s => s.PasswordProtected).HasMaxLength(2048);
        b.Property(s => s.BaseUrl).HasMaxLength(256);
        b.Property(s => s.DeliveryTime).HasMaxLength(64);
        b.Property(s => s.LastImportSummary).HasMaxLength(4000);
    }
}
