using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Vpn.Domain;

namespace Sentinel.Vpn.Persistence;

public sealed class ServiceMigrationConfiguration : IEntityTypeConfiguration<ServiceMigration>
{
    public void Configure(EntityTypeBuilder<ServiceMigration> builder)
    {
        builder.ToTable("ServiceMigrations");

        builder.HasKey(migration => migration.Id);

        builder.Property(migration => migration.Step).HasConversion<int>().IsRequired();
        builder.Property(migration => migration.LastError).HasMaxLength(ServiceMigration.ErrorMaxLength);
        builder.Property(migration => migration.Reason).HasMaxLength(500);

        // How two replicas avoid advancing the same migration: claiming a step is a guarded write.
        builder.Property(migration => migration.ConcurrencyToken).IsConcurrencyToken();

        builder.HasOne(migration => migration.Service)
            .WithMany()
            .HasForeignKey(migration => migration.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Neither server carries a foreign key. A migration is a historical record and has to survive
        // a server being withdrawn — which, given "move everyone off this box and delete it" is the
        // commonest reason to run one, would otherwise delete exactly the records worth keeping.

        // The executor's claim query.
        builder.HasIndex(migration => new { migration.Step, migration.NextAttemptAt });

        builder.HasIndex(migration => migration.ServiceId);

        // "What is still in flight on this server", which is what an operator draining one asks.
        builder.HasIndex(migration => new { migration.DestinationServerId, migration.Step });
        builder.HasIndex(migration => new { migration.SourceServerId, migration.Step });
    }
}
