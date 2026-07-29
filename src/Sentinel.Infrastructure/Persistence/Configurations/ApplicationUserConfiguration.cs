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

        builder.Property(u => u.NormalizedPhoneNumber)
            .HasMaxLength(ApplicationUser.NormalizedPhoneMaxLength);

        builder.Property(u => u.Status)
            .HasConversion<int>()
            .IsRequired();

        // Admin list filters on status and sorts by creation date.
        builder.HasIndex(u => u.Status);
        builder.HasIndex(u => u.CreatedAt);

        // One account per phone number. PostgreSQL and SQLite treat NULLs as distinct, so
        // accounts without a phone are unaffected; SQL Server needs a filtered index, which
        // SentinelDbContext adds for that provider only.
        builder.HasIndex(u => u.NormalizedPhoneNumber).IsUnique();

        builder.Property(u => u.TelegramUsername)
            .HasMaxLength(ApplicationUser.TelegramUsernameMaxLength);

        // One Telegram account per portal account, for the same reason as the phone number:
        // otherwise one Telegram chat could receive two members' notifications.
        builder.HasIndex(u => u.TelegramUserId).IsUnique();

        builder.HasOne(u => u.Membership)
            .WithOne(m => m.User!)
            .HasForeignKey<Domain.Memberships.Membership>(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
