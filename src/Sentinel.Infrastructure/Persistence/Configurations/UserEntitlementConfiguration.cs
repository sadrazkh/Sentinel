using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Entitlements;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class UserEntitlementConfiguration : IEntityTypeConfiguration<UserEntitlement>
{
    public void Configure(EntityTypeBuilder<UserEntitlement> builder)
    {
        builder.ToTable("UserEntitlements");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Notes).HasMaxLength(UserEntitlement.NotesMaxLength);
        builder.Property(e => e.ConcurrencyToken).IsConcurrencyToken();

        builder.HasOne(e => e.User)
            .WithMany(u => u.Entitlements)
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Applications are retired, not deleted; refuse a delete that would drop live grants.
        builder.HasOne(e => e.Application)
            .WithMany(a => a.Entitlements)
            .HasForeignKey(e => e.ApplicationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Exactly one row per (user, application). The access check is then a single lookup
        // and re-granting cannot produce two rows that disagree with each other.
        builder.HasIndex(e => new { e.UserId, e.ApplicationId }).IsUnique();

        builder.HasIndex(e => e.ApplicationId);
    }
}
