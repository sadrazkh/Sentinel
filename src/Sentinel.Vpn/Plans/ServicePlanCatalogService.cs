using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Features;
using Sentinel.Application.Memberships;
using Sentinel.Application.Users;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Persistence;

namespace Sentinel.Vpn.Plans;

/// <summary>
/// The plans one member is offered for one product.
/// <para>
/// Loads the plans and their rules, then defers to <see cref="PlanAudienceEvaluator"/>. The audience
/// decision is not re-derived anywhere else, and the projection that reaches the browser carries no
/// trace of it: a plan withheld looks exactly like a plan that does not exist.
/// </para>
/// </summary>
public sealed class ServicePlanCatalogService : IServicePlanCatalog
{
    private readonly IVpnDbContext _vpn;
    private readonly ISentinelDbContext _db;
    private readonly IMembershipStatusResolver _membershipResolver;
    private readonly IMemberRoleQuery _roles;
    private readonly IFeatureGate _features;
    private readonly TimeProvider _timeProvider;

    public ServicePlanCatalogService(
        IVpnDbContext vpn,
        ISentinelDbContext db,
        IMembershipStatusResolver membershipResolver,
        IMemberRoleQuery roles,
        IFeatureGate features,
        TimeProvider timeProvider)
    {
        _vpn = vpn;
        _db = db;
        _membershipResolver = membershipResolver;
        _roles = roles;
        _features = features;
        _timeProvider = timeProvider;
    }

    public async Task<ServicePlanCatalog> GetForMemberAsync(
        Guid userId,
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        var subject = await LoadSubjectAsync(userId, cancellationToken);

        if (subject is null)
        {
            return ServicePlanCatalog.Empty;
        }

        // Hidden plans are filtered in SQL: a plan an operator has withdrawn is not something a
        // member should receive the row for, whatever the audience rules would say.
        var rows = await _vpn.ServicePlans
            .AsNoTracking()
            .Where(plan => plan.ProductId == productId && plan.IsVisible)
            .OrderByDescending(plan => plan.IsFeatured)
            .ThenBy(plan => plan.DisplayOrder)
            .ThenBy(plan => plan.PriceMinorUnits)
            .Select(plan => new PlanRow(
                plan.Id, plan.Key, plan.NameFa, plan.NameEn,
                plan.DescriptionFa, plan.DescriptionEn,
                plan.TrafficBytes, plan.DurationDays, plan.DeviceLimit,
                plan.PriceMinorUnits, plan.Currency, plan.CountryCode,
                plan.IsFeatured, plan.DisplayOrder, plan.IsPurchasable,
                plan.AudienceRules
                    .Select(rule => new AudienceRuleFacts(
                        rule.Effect, rule.Kind, rule.Tier, rule.RoleName, rule.UserId))
                    .ToList()))
            .ToListAsync(cancellationToken);

        // Purchasing needs the feature as well as the plan's own switch. The feature ships off, so
        // today this is false for every plan and the catalogue is a price list.
        var purchasingEnabled = _features.IsEnabled(FeatureNames.Purchases);

        var visible = rows
            .Where(row => PlanAudienceEvaluator.IsInAudience(subject, row.Rules))
            .Select(row => new ServicePlanCard(
                row.Id, row.Key, row.NameFa, row.NameEn,
                row.DescriptionFa, row.DescriptionEn,
                row.TrafficBytes, row.DurationDays, row.DeviceLimit,
                row.PriceMinorUnits, row.Currency, row.CountryCode,
                row.IsFeatured, row.DisplayOrder,
                CanOrder: purchasingEnabled && row.IsPurchasable))
            .ToList();

        return new ServicePlanCatalog(
            visible,
            await AvailableCountriesAsync(visible, cancellationToken),
            purchasingEnabled);
    }

    /// <summary>
    /// The countries the portal could actually deliver in right now.
    /// <para>
    /// Intersected with real server state rather than taken from the plans alone: offering a
    /// location whose only server is full or unreachable produces a failure at provisioning time,
    /// which is the worst moment for a customer to find out.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> AvailableCountriesAsync(
        IReadOnlyList<ServicePlanCard> plans,
        CancellationToken cancellationToken)
    {
        if (plans.Count == 0)
        {
            return [];
        }

        // A server needs to be active, reachable, have room, and have at least one enabled inbound
        // — all four, or it cannot take a service.
        var ready = await _vpn.VpnServers
            .AsNoTracking()
            .Where(server => server.Status == VpnServerStatus.Active
                             && server.Health != VpnServerHealth.Unreachable
                             && server.ReservedClients < server.MaxClients
                             && server.InboundProfiles.Any(profile => profile.IsEnabled))
            .Select(server => server.CountryCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        var readySet = ready.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A plan with no country asks for any location, so it makes every ready country relevant.
        if (plans.Any(plan => plan.CountryCode is null))
        {
            return ready.Order(StringComparer.Ordinal).ToList();
        }

        return plans
            .Select(plan => plan.CountryCode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(readySet.Contains)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The member's tier and roles.
    /// <para>
    /// The tier comes from the resolved membership snapshot, not the raw row: a lapsed Elite
    /// membership must not match an Elite-only plan, and the resolver is the one place that decides
    /// whether a membership currently grants anything.
    /// </para>
    /// </summary>
    private async Task<AudienceSubject?> LoadSubjectAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        var facts = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new
            {
                Membership = user.Membership == null
                    ? null
                    : new MembershipFacts(
                        user.Membership.Tier,
                        user.Membership.AdminState,
                        user.Membership.StartsAt,
                        user.Membership.EndsAt,
                        user.Membership.GracePeriodDaysOverride),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (facts is null)
        {
            return null;
        }

        var snapshot = _membershipResolver.Resolve(facts.Membership, now);

        var roles = await _roles.GetRoleNamesAsync(userId, cancellationToken);

        return new AudienceSubject(
            userId,
            snapshot.GrantsAccess ? snapshot.Tier : null,
            roles);
    }

    private sealed record PlanRow(
        Guid Id,
        string Key,
        string NameFa,
        string NameEn,
        string? DescriptionFa,
        string? DescriptionEn,
        long TrafficBytes,
        int DurationDays,
        int DeviceLimit,
        long PriceMinorUnits,
        string Currency,
        string? CountryCode,
        bool IsFeatured,
        int DisplayOrder,
        bool IsPurchasable,
        List<AudienceRuleFacts> Rules);
}
