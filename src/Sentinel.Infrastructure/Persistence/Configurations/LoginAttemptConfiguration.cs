using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Security;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class LoginAttemptConfiguration : IEntityTypeConfiguration<LoginAttempt>
{
    public void Configure(EntityTypeBuilder<LoginAttempt> builder)
    {
        builder.ToTable("LoginAttempts");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.AttemptedIdentifier)
            .HasMaxLength(LoginAttempt.IdentifierMaxLength)
            .IsRequired();

        builder.Property(a => a.IpAddress).HasMaxLength(LoginAttempt.IpAddressMaxLength);
        builder.Property(a => a.UserAgent).HasMaxLength(LoginAttempt.UserAgentMaxLength);
        builder.Property(a => a.FailureReason).HasConversion<int>().IsRequired();

        builder.HasOne(a => a.User)
            .WithMany(u => u.LoginAttempts)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Member-facing login history.
        builder.HasIndex(a => new { a.UserId, a.OccurredAt });

        // Credential-stuffing analysis: failures per identifier and per source address.
        builder.HasIndex(a => new { a.AttemptedIdentifier, a.OccurredAt });
        builder.HasIndex(a => new { a.IpAddress, a.OccurredAt });
    }
}
