using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Entitlements;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class ProductEntitlementConfiguration : IEntityTypeConfiguration<ProductEntitlement>
{
    public void Configure(EntityTypeBuilder<ProductEntitlement> builder)
    {
        builder.ToTable("ProductEntitlements");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Notes).HasMaxLength(ProductEntitlement.NotesMaxLength);
        builder.Property(e => e.Source).HasConversion<int>().IsRequired();
        builder.Property(e => e.ConcurrencyToken).IsConcurrencyToken();

        builder.HasOne(e => e.User)
            .WithMany(u => u.Entitlements)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Products are deprecated or archived, not deleted; refuse a delete that would drop
        // live grants and leave members wondering where their access went.
        builder.HasOne(e => e.Product)
            .WithMany(p => p.Entitlements)
            .HasForeignKey(e => e.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // Exactly one row per (member, product). The access check is then a single lookup and
        // re-granting cannot produce two rows that disagree with each other.
        builder.HasIndex(e => new { e.UserId, e.ProductId }).IsUnique();

        builder.HasIndex(e => e.ProductId);
    }
}
