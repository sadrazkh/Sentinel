using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Auditing;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.ActorUserName).HasMaxLength(AuditLog.ActorNameMaxLength);
        builder.Property(a => a.Action).HasMaxLength(AuditLog.ActionMaxLength).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(AuditLog.EntityTypeMaxLength).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(AuditLog.EntityIdMaxLength);
        builder.Property(a => a.IpAddress).HasMaxLength(AuditLog.IpAddressMaxLength);
        builder.Property(a => a.UserAgent).HasMaxLength(AuditLog.UserAgentMaxLength);
        builder.Property(a => a.CorrelationId).HasMaxLength(AuditLog.CorrelationIdMaxLength);
        builder.Property(a => a.MetadataJson).HasMaxLength(AuditLog.MetadataMaxLength);
        builder.Property(a => a.Result).HasConversion<int>().IsRequired();

        // Audit history outlives the account it refers to.
        builder.HasOne(a => a.ActorUser)
            .WithMany(u => u.ActedAuditLogs)
            .HasForeignKey(a => a.ActorUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // The viewer pages by time, and drills down by actor, by action, or by target entity.
        builder.HasIndex(a => a.OccurredAt);
        builder.HasIndex(a => new { a.ActorUserId, a.OccurredAt });
        builder.HasIndex(a => new { a.Action, a.OccurredAt });
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
    }
}
