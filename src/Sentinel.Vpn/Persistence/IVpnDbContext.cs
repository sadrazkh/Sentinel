using Microsoft.EntityFrameworkCore;
using Sentinel.Vpn.Domain;

namespace Sentinel.Vpn.Persistence;

/// <summary>
/// The persistence surface the VPN module needs.
/// <para>
/// A narrow interface of its own rather than a generic <c>Set&lt;T&gt;()</c> on the shared context:
/// the module says exactly which tables it touches, and the shared abstraction does not gain an
/// escape hatch that would let any layer reach any entity.
/// </para>
/// <para>
/// Implemented by the same <c>SentinelDbContext</c>, so a VPN write and a shared-catalogue write
/// still commit in one transaction — which provisioning and, later, the wallet depend on.
/// </para>
/// </summary>
public interface IVpnDbContext
{
    DbSet<VpnServer> VpnServers { get; }

    DbSet<ServerInboundProfile> ServerInboundProfiles { get; }

    DbSet<ServicePlan> ServicePlans { get; }

    DbSet<PlanAudienceRule> PlanAudienceRules { get; }

    DbSet<CustomerService> CustomerServices { get; }

    DbSet<ServiceInboundBinding> ServiceInboundBindings { get; }

    DbSet<ProvisioningJob> ProvisioningJobs { get; }

    DbSet<ServiceMigration> ServiceMigrations { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-reads a tracked entity from the database, discarding local changes.
    /// <para>
    /// Needed after a concurrency conflict: the tracked copy still carries the original token, so
    /// retrying with it would resubmit the same losing UPDATE. Exposed here rather than casting the
    /// interface back to <c>DbContext</c> at the call site, which would defeat the point of having a
    /// narrow contract.
    /// </para>
    /// </summary>
    Task ReloadAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default)
        where TEntity : class;
}
