using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Identity;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.ToTable("Users");

        builder.Property(u => u.DisplayName)
            .HasMaxLength(ApplicationUser.DisplayNameMaxLength)
            .IsRequired();

        builder.Property(u => u.StatusNote)
            .HasMaxLength(ApplicationUser.StatusNoteMaxLength);

        builder.Property(u => u.PreferredCulture)
            .HasMaxLength(ApplicationUser.CultureMaxLength)
            .IsRequired();

        builder.Property(u => u.TimeZoneId)
            .HasMaxLength(ApplicationUser.TimeZoneMaxLength)
            .IsRequired();

        builder.Property(u => u.Status)
            .HasConversion<int>()
            .IsRequired();

        // Admin list filters on status and sorts by creation date.
        builder.HasIndex(u => u.Status);
        builder.HasIndex(u => u.CreatedAt);

        builder.HasOne(u => u.Membership)
            .WithOne(m => m.User!)
            .HasForeignKey<Domain.Memberships.Membership>(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
