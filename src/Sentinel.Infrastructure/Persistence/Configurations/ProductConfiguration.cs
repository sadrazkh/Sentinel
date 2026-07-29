using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Products;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Key).HasMaxLength(Product.KeyMaxLength).IsRequired();
        builder.Property(p => p.NameFa).HasMaxLength(Product.NameMaxLength).IsRequired();
        builder.Property(p => p.NameEn).HasMaxLength(Product.NameMaxLength).IsRequired();
        builder.Property(p => p.SummaryFa).HasMaxLength(Product.SummaryMaxLength);
        builder.Property(p => p.SummaryEn).HasMaxLength(Product.SummaryMaxLength);
        builder.Property(p => p.DescriptionFa).HasMaxLength(Product.DescriptionMaxLength);
        builder.Property(p => p.DescriptionEn).HasMaxLength(Product.DescriptionMaxLength);
        builder.Property(p => p.IconPath).HasMaxLength(Product.MediaPathMaxLength);
        builder.Property(p => p.CoverPath).HasMaxLength(Product.MediaPathMaxLength);
        builder.Property(p => p.CurrentVersion).HasMaxLength(Product.VersionMaxLength);

        // Optional: only a Launchable product needs somewhere to go.
        builder.Property(p => p.LaunchUrl).HasMaxLength(Product.LaunchUrlMaxLength);

        builder.Property(p => p.Type).HasConversion<int>().IsRequired();
        builder.Property(p => p.ReleaseStatus).HasConversion<int>().IsRequired();
        builder.Property(p => p.MinimumTier).HasConversion<int?>();

        // Stored as an int bitmask so capability filters stay in SQL rather than becoming a
        // post-fetch pass over the whole catalogue.
        builder.Property(p => p.Capabilities).HasConversion<int>().IsRequired();

        builder.Property(p => p.ConcurrencyToken).IsConcurrencyToken();

        builder.HasIndex(p => p.Key).IsUnique();

        // The catalogue query filters on release status + enabled and orders by display order.
        builder.HasIndex(p => new { p.ReleaseStatus, p.IsEnabled, p.DisplayOrder });
        builder.HasIndex(p => p.CategoryId);

        // A category is presentation; removing one must never cascade into products.
        builder.HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class ProductCategoryConfiguration : IEntityTypeConfiguration<ProductCategory>
{
    public void Configure(EntityTypeBuilder<ProductCategory> builder)
    {
        builder.ToTable("ProductCategories");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Key).HasMaxLength(ProductCategory.KeyMaxLength).IsRequired();
        builder.Property(c => c.NameFa).HasMaxLength(ProductCategory.NameMaxLength).IsRequired();
        builder.Property(c => c.NameEn).HasMaxLength(ProductCategory.NameMaxLength).IsRequired();
        builder.Property(c => c.IconName).HasMaxLength(64);

        builder.HasIndex(c => c.Key).IsUnique();
    }
}
