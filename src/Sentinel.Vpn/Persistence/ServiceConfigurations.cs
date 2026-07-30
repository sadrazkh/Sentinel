using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Vpn.Domain;

namespace Sentinel.Vpn.Persistence;

public sealed class CustomerServiceConfiguration : IEntityTypeConfiguration<CustomerService>
{
    public void Configure(EntityTypeBuilder<CustomerService> builder)
    {
        builder.ToTable("CustomerServices");

        builder.HasKey(service => service.Id);

        builder.Property(service => service.PlanNameFa).HasMaxLength(128).IsRequired();
        builder.Property(service => service.PlanNameEn).HasMaxLength(128).IsRequired();
        builder.Property(service => service.Notes).HasMaxLength(CustomerService.NotesMaxLength);
        builder.Property(service => service.LastError).HasMaxLength(CustomerService.ErrorMaxLength);

        // Long enough for the panel identifier this portal mints ("s-" plus 16 hex).
        builder.Property(service => service.PanelClientEmail).HasMaxLength(64);

        // Hex SHA-256.
        builder.Property(service => service.DeliveryTokenHash).HasMaxLength(64);

        // Data-protection ciphertext of a 43-character token. Roomy on purpose: the envelope carries
        // a key id and a version, and a future payload format must not need a schema change.
        builder.Property(service => service.DeliveryTokenSealed).HasMaxLength(512);

        builder.Property(service => service.Status).HasConversion<int>().IsRequired();

        builder.Property(service => service.ConcurrencyToken).IsConcurrencyToken();

        // A service's server may be withdrawn; the row must survive so the history and the customer's
        // record do not vanish with it.
        builder.HasOne(service => service.Server)
            .WithMany()
            .HasForeignKey(service => service.ServerId)
            .OnDelete(DeleteBehavior.SetNull);

        // The member's own list, and the library's "do they have this product" check.
        builder.HasIndex(service => new { service.UserId, service.Status });
        builder.HasIndex(service => new { service.UserId, service.ProductId });

        // The delivery endpoint's only lookup, and it is on an anonymous path — so it has to be an
        // index seek rather than a scan.
        builder.HasIndex(service => service.DeliveryTokenHash);

        // The expiry sweep.
        builder.HasIndex(service => new { service.Status, service.ExpiresAt });

        // Reconciliation walks a server's services.
        builder.HasIndex(service => new { service.ServerId, service.Status });

        // One panel identifier per server. Two services sharing one would each believe the other's
        // traffic was theirs.
        builder.HasIndex(service => new { service.ServerId, service.PanelClientEmail }).IsUnique();
    }
}

public sealed class ServiceInboundBindingConfiguration : IEntityTypeConfiguration<ServiceInboundBinding>
{
    public void Configure(EntityTypeBuilder<ServiceInboundBinding> builder)
    {
        builder.ToTable("ServiceInboundBindings");

        builder.HasKey(binding => binding.Id);

        builder.Property(binding => binding.State).HasConversion<int>().IsRequired();
        builder.Property(binding => binding.ConcurrencyToken).IsConcurrencyToken();

        builder.HasOne(binding => binding.Service)
            .WithMany(service => service.Bindings)
            .HasForeignKey(binding => binding.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per (service, server, inbound). During a migration a service legitimately has
        // bindings on two servers, so the server is part of the key rather than assumed.
        builder.HasIndex(binding => new { binding.ServiceId, binding.ServerId, binding.InboundId })
            .IsUnique();

        builder.HasIndex(binding => new { binding.ServerId, binding.State });
    }
}

public sealed class ProvisioningJobConfiguration : IEntityTypeConfiguration<ProvisioningJob>
{
    public void Configure(EntityTypeBuilder<ProvisioningJob> builder)
    {
        builder.ToTable("ProvisioningJobs");

        builder.HasKey(job => job.Id);

        builder.Property(job => job.Kind).HasConversion<int>().IsRequired();
        builder.Property(job => job.Status).HasConversion<int>().IsRequired();
        builder.Property(job => job.LastError).HasMaxLength(ProvisioningJob.ErrorMaxLength);

        // How two replicas avoid running the same job: claiming it is a write, and the loser of the
        // race fails on the token rather than duplicating a panel call.
        builder.Property(job => job.ConcurrencyToken).IsConcurrencyToken();

        builder.HasOne(job => job.Service)
            .WithMany()
            .HasForeignKey(job => job.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The worker's claim query: runnable jobs, oldest first.
        builder.HasIndex(job => new { job.Status, job.NextAttemptAt });

        builder.HasIndex(job => job.ServiceId);
    }
}
