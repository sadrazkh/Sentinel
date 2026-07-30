using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Persistence;

namespace Sentinel.Vpn.Plans;

/// <summary>The operator's read side for plans. Carries the audience rules, which a member never sees.</summary>
public sealed class ServicePlanAdminQuery : IServicePlanAdminQuery
{
    private readonly IVpnDbContext _vpn;
    private readonly ISentinelDbContext _db;

    public ServicePlanAdminQuery(IVpnDbContext vpn, ISentinelDbContext db)
    {
        _vpn = vpn;
        _db = db;
    }

    public async Task<IReadOnlyList<ServicePlanListItem>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var plans = await _vpn.ServicePlans
            .AsNoTracking()
            .OrderBy(plan => plan.ProductId)
            .ThenBy(plan => plan.DisplayOrder)
            .ThenBy(plan => plan.PriceMinorUnits)
            .Select(plan => new
            {
                plan.Id,
                plan.Key,
                plan.ProductId,
                plan.NameFa,
                plan.NameEn,
                plan.TrafficBytes,
                plan.DurationDays,
                plan.DeviceLimit,
                plan.PriceMinorUnits,
                plan.Currency,
                plan.IsVisible,
                plan.IsPurchasable,
                plan.CountryCode,
                plan.DisplayOrder,
                plan.IsFeatured,

                // Counted in SQL rather than by loading the rules: the list shows two numbers.
                AllowRules = plan.AudienceRules.Count(rule => rule.Effect == AudienceEffect.Allow),
                DenyRules = plan.AudienceRules.Count(rule => rule.Effect == AudienceEffect.Deny),
            })
            .ToListAsync(cancellationToken);

        if (plans.Count == 0)
        {
            return [];
        }

        // Product names come from the shared catalogue in one extra query rather than a join across
        // the module boundary, then are matched in memory. The plan count is small by construction.
        var productIds = plans.Select(plan => plan.ProductId).Distinct().ToList();

        var products = await _db.Products
            .AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new { product.Id, product.NameFa, product.NameEn })
            .ToListAsync(cancellationToken);

        var byId = products.ToDictionary(product => product.Id);

        return plans
            .Select(plan =>
            {
                byId.TryGetValue(plan.ProductId, out var product);

                return new ServicePlanListItem(
                    plan.Id,
                    plan.Key,
                    plan.ProductId,
                    // A plan whose product was deleted is a broken row an operator needs to see,
                    // not one the list should hide.
                    product?.NameFa ?? "—",
                    product?.NameEn ?? "—",
                    plan.NameFa,
                    plan.NameEn,
                    plan.TrafficBytes,
                    plan.DurationDays,
                    plan.DeviceLimit,
                    plan.PriceMinorUnits,
                    plan.Currency,
                    plan.IsVisible,
                    plan.IsPurchasable,
                    plan.CountryCode,
                    plan.DisplayOrder,
                    plan.IsFeatured,
                    plan.AllowRules,
                    plan.DenyRules);
            })
            .ToList();
    }

    public async Task<ServicePlanEditModel?> GetForEditAsync(
        Guid planId,
        CancellationToken cancellationToken = default)
    {
        var plan = await _vpn.ServicePlans
            .AsNoTracking()
            .Where(candidate => candidate.Id == planId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Key,
                candidate.ProductId,
                candidate.NameFa,
                candidate.NameEn,
                candidate.DescriptionFa,
                candidate.DescriptionEn,
                candidate.TrafficBytes,
                candidate.DurationDays,
                candidate.DeviceLimit,
                candidate.PriceMinorUnits,
                candidate.Currency,
                candidate.IsVisible,
                candidate.IsPurchasable,
                candidate.CountryCode,
                candidate.DisplayOrder,
                candidate.IsFeatured,
                candidate.ConcurrencyToken,
                Rules = candidate.AudienceRules
                    // Denies first: they are the ones that override, so they belong at the top of
                    // the list an operator reads.
                    .OrderBy(rule => rule.Effect == AudienceEffect.Deny ? 0 : 1)
                    .ThenBy(rule => rule.Kind)
                    .Select(rule => new AudienceRuleRow(
                        rule.Id, rule.Effect, rule.Kind, rule.Tier,
                        rule.RoleName, rule.UserId, rule.Note))
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (plan is null)
        {
            return null;
        }

        return new ServicePlanEditModel(
            plan.Id,
            plan.Key,
            plan.ProductId,
            plan.NameFa,
            plan.NameEn,
            plan.DescriptionFa,
            plan.DescriptionEn,
            plan.TrafficBytes,
            plan.DurationDays,
            plan.DeviceLimit,
            plan.PriceMinorUnits,
            plan.Currency,
            plan.IsVisible,
            plan.IsPurchasable,
            plan.CountryCode,
            plan.DisplayOrder,
            plan.IsFeatured,
            plan.Rules,
            plan.ConcurrencyToken);
    }
}
