using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Security;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("UserSessions");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.IpAddress).HasMaxLength(UserSession.IpAddressMaxLength);
        builder.Property(s => s.UserAgent).HasMaxLength(UserSession.UserAgentMaxLength);
        builder.Property(s => s.RevocationReason).HasConversion<int>().IsRequired();

        builder.HasOne(s => s.User)
            .WithMany(u => u.Sessions)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // "List my active sessions" and "revoke everything for this user".
        builder.HasIndex(s => new { s.UserId, s.RevokedAt });

        // Cleanup of expired rows.
        builder.HasIndex(s => s.ExpiresAt);
    }
}
