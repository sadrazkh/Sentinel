using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Catalog;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class PortalApplicationConfiguration : IEntityTypeConfiguration<PortalApplication>
{
    public void Configure(EntityTypeBuilder<PortalApplication> builder)
    {
        builder.ToTable("PortalApplications");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Key)
            .HasMaxLength(PortalApplication.KeyMaxLength)
            .IsRequired();

        builder.Property(a => a.NameFa).HasMaxLength(PortalApplication.NameMaxLength).IsRequired();
        builder.Property(a => a.NameEn).HasMaxLength(PortalApplication.NameMaxLength).IsRequired();
        builder.Property(a => a.DescriptionFa).HasMaxLength(PortalApplication.DescriptionMaxLength);
        builder.Property(a => a.DescriptionEn).HasMaxLength(PortalApplication.DescriptionMaxLength);
        builder.Property(a => a.IconPath).HasMaxLength(PortalApplication.IconPathMaxLength);

        builder.Property(a => a.LaunchUrl)
            .HasMaxLength(PortalApplication.LaunchUrlMaxLength)
            .IsRequired();

        builder.Property(a => a.PublishStatus).HasConversion<int>().IsRequired();
        builder.Property(a => a.MinimumTier).HasConversion<int?>();

        builder.Property(a => a.ConcurrencyToken).IsConcurrencyToken();

        builder.HasIndex(a => a.Key).IsUnique();

        // The catalogue query filters on publish status + enabled and orders by display order.
        builder.HasIndex(a => new { a.PublishStatus, a.IsEnabled, a.DisplayOrder });
    }
}
