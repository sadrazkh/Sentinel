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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
