using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Memberships;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.ToTable("Memberships");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Tier).HasConversion<int>().IsRequired();
        builder.Property(m => m.AdminState).HasConversion<int>().IsRequired();
        builder.Property(m => m.Notes).HasMaxLength(Membership.NotesMaxLength);

        builder.Property(m => m.ConcurrencyToken).IsConcurrencyToken();

        // One membership per user: renewals mutate this row, history lives in the audit log.
        builder.HasIndex(m => m.UserId).IsUnique();

        // "Who expires soon?" and grace-period sweeps scan on the end date.
        builder.HasIndex(m => m.EndsAt);
    }
}
