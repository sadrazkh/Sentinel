using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Catalog;
using Sentinel.Application.Common;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Servers;

namespace Sentinel.Vpn.Plans;

/// <summary>
/// Authoring for service plans and their audience rules.
/// <para>
/// Every term a customer is sold — traffic, duration, devices, price — is written only here, by an
/// operator. There is no path from a request to any of these numbers.
/// </para>
/// </summary>
public sealed class ServicePlanAdminService : IServicePlanAdminService
{
    /// <summary>
    /// A year. Long enough for any real plan and short enough that a mistyped duration is caught
    /// rather than sold.
    /// </summary>
    private const int MaxDurationDays = 3650;

    private readonly IVpnDbContext _vpn;
    private readonly ISentinelDbContext _db;
    private readonly IAuditService _audit;
    private readonly TimeProvider _timeProvider;

    public ServicePlanAdminService(
        IVpnDbContext vpn,
        ISentinelDbContext db,
        IAuditService audit,
        TimeProvider timeProvider)
    {
        _vpn = vpn;
        _db = db;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    public async Task<OperationResult<Guid>> SaveAsync(
        Guid? planId,
        ServicePlanSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = request.Key.Trim().ToLowerInvariant();

        if (!ApplicationKey.IsValid(key))
        {
            return OperationResult<Guid>.Failure(PlanErrors.KeyInvalid);
        }

        // The product lives in the shared catalogue, so there is no foreign key to lean on — the
        // check is explicit here instead.
        if (!await _db.Products.AnyAsync(product => product.Id == request.ProductId, cancellationToken))
        {
            return OperationResult<Guid>.Failure(PlanErrors.ProductNotFound);
        }

        if (request.DurationDays is < 1 or > MaxDurationDays)
        {
            return OperationResult<Guid>.Failure(PlanErrors.DurationInvalid);
        }

        // Negative traffic, price or device count are not "unlimited" — they are a typo, and letting
        // one through would sell a plan whose terms nobody meant.
        if (request.TrafficBytes < 0 || request.PriceMinorUnits < 0 || request.DeviceLimit < 0)
        {
            return OperationResult<Guid>.Failure(PlanErrors.NegativeAmount);
        }

        var currency = request.Currency.Trim().ToUpperInvariant();

        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            return OperationResult<Guid>.Failure(PlanErrors.CurrencyInvalid);
        }

        string? country = null;

        if (!string.IsNullOrWhiteSpace(request.CountryCode))
        {
            country = request.CountryCode.Trim().ToUpperInvariant();

            if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
            {
                return OperationResult<Guid>.Failure(PlanErrors.CountryInvalid);
            }
        }

        var isNew = planId is null;
        ServicePlan plan;

        if (planId is { } id)
        {
            var existing = await _vpn.ServicePlans
                .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

            if (existing is null)
            {
                return OperationResult<Guid>.Failure(PlanErrors.NotFound);
            }

            if (request.ConcurrencyToken is { } token && existing.ConcurrencyToken != token)
            {
                return OperationResult<Guid>.Failure(OperationErrors.ConcurrencyConflict);
            }

            plan = existing;
        }
        else
        {
            plan = new ServicePlan
            {
                Id = SequentialGuid.New(_timeProvider.GetUtcNow()),
            };

            _vpn.ServicePlans.Add(plan);
        }

        if (await _vpn.ServicePlans
                .AnyAsync(candidate => candidate.Key == key && candidate.Id != plan.Id, cancellationToken))
        {
            return OperationResult<Guid>.Failure(PlanErrors.KeyTaken);
        }

        plan.Key = key;
        plan.ProductId = request.ProductId;
        plan.NameFa = request.NameFa.Trim();
        plan.NameEn = request.NameEn.Trim();
        plan.DescriptionFa = Trim(request.DescriptionFa);
        plan.DescriptionEn = Trim(request.DescriptionEn);
        plan.TrafficBytes = request.TrafficBytes;
        plan.DurationDays = request.DurationDays;
        plan.DeviceLimit = request.DeviceLimit;
        plan.PriceMinorUnits = request.PriceMinorUnits;
        plan.Currency = currency;
        plan.IsVisible = request.IsVisible;
        plan.IsPurchasable = request.IsPurchasable;
        plan.CountryCode = country;
        plan.DisplayOrder = request.DisplayOrder;
        plan.IsFeatured = request.IsFeatured;

        // At most one featured plan per product: two "recommended" options recommend nothing, and
        // clearing the others here is less surprising than refusing the save.
        if (request.IsFeatured)
        {
            var others = await _vpn.ServicePlans
                .Where(candidate => candidate.ProductId == request.ProductId
                                    && candidate.Id != plan.Id
                                    && candidate.IsFeatured)
                .ToListAsync(cancellationToken);

            foreach (var other in others)
            {
                other.IsFeatured = false;
            }
        }

        await _audit.RecordAsync(
            AuditEntry.For(
                isNew ? VpnAuditActions.PlanCreated : VpnAuditActions.PlanUpdated,
                nameof(ServicePlan),
                plan.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("planSlug", plan.Key)
                    .Set("productId", plan.ProductId)
                    .Set("trafficBytes", plan.TrafficBytes)
                    .Set("durationDays", plan.DurationDays)
                    .Set("deviceLimit", plan.DeviceLimit)
                    // The terms of a sale, so the price belongs in the record of who changed what.
                    .Set("priceMinorUnits", plan.PriceMinorUnits)
                    .Set("currency", plan.Currency)
                    .Set("visible", plan.IsVisible)
                    .Set("purchasable", plan.IsPurchasable),
            },
            cancellationToken);

        await _vpn.SaveChangesAsync(cancellationToken);

        return OperationResult<Guid>.Success(plan.Id);
    }

    public async Task<OperationResult> DeleteAsync(
        Guid planId,
        CancellationToken cancellationToken = default)
    {
        var plan = await _vpn.ServicePlans
            .FirstOrDefaultAsync(candidate => candidate.Id == planId, cancellationToken);

        if (plan is null)
        {
            return OperationResult.Failure(PlanErrors.NotFound);
        }

        // Rules go with it through the cascade. Deleting a plan is safe today because nothing
        // references one yet; once customer services do, this becomes a soft withdrawal instead.
        _vpn.ServicePlans.Remove(plan);

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.PlanDeleted, nameof(ServicePlan), planId) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("planSlug", plan.Key)
                    .Set("productId", plan.ProductId),
            },
            cancellationToken);

        await _vpn.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> AddRuleAsync(
        Guid planId,
        AudienceRuleSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await _vpn.ServicePlans.AnyAsync(plan => plan.Id == planId, cancellationToken))
        {
            return OperationResult.Failure(PlanErrors.NotFound);
        }

        // A half-filled rule matches nothing, so it would sit in the list looking like a
        // restriction while doing nothing. Refused rather than stored.
        if (!IsComplete(request))
        {
            return OperationResult.Failure(PlanErrors.RuleIncomplete);
        }

        var now = _timeProvider.GetUtcNow();

        _vpn.PlanAudienceRules.Add(new PlanAudienceRule
        {
            Id = SequentialGuid.New(now),
            PlanId = planId,
            Effect = request.Effect,
            Kind = request.Kind,
            Tier = request.Kind is AudienceRuleKind.MembershipTier or AudienceRuleKind.MinimumTier
                ? request.Tier
                : null,
            RoleName = request.Kind == AudienceRuleKind.Role ? request.RoleName?.Trim() : null,
            UserId = request.Kind == AudienceRuleKind.User ? request.UserId : null,
            Note = Trim(request.Note),
        });

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.PlanRuleAdded, nameof(PlanAudienceRule), planId) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("effect", request.Effect)
                    .Set("kind", request.Kind)
                    .Set("tier", request.Tier)
                    .Set("role", request.RoleName),
            },
            cancellationToken);

        await _vpn.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveRuleAsync(
        Guid ruleId,
        CancellationToken cancellationToken = default)
    {
        var rule = await _vpn.PlanAudienceRules
            .FirstOrDefaultAsync(candidate => candidate.Id == ruleId, cancellationToken);

        if (rule is null)
        {
            return OperationResult.Failure(PlanErrors.NotFound);
        }

        _vpn.PlanAudienceRules.Remove(rule);

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.PlanRuleRemoved, nameof(PlanAudienceRule), ruleId) with
            {
                // Removing a deny widens who can buy something, so what it was matters.
                Metadata = AuditMetadata.Create()
                    .Set("planId", rule.PlanId)
                    .Set("effect", rule.Effect)
                    .Set("kind", rule.Kind),
            },
            cancellationToken);

        await _vpn.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    /// <summary>
    /// Whether a rule carries the value its kind needs. Mirrors the evaluator's matcher: anything it
    /// would silently ignore is refused here instead.
    /// </summary>
    private static bool IsComplete(AudienceRuleSaveRequest request) => request.Kind switch
    {
        AudienceRuleKind.Everyone => true,
        AudienceRuleKind.MembershipTier or AudienceRuleKind.MinimumTier => request.Tier is not null,
        AudienceRuleKind.Role => !string.IsNullOrWhiteSpace(request.RoleName),

        // Guid.Empty is what an unfilled form field binds to, and it would match no member — so it
        // is treated as missing rather than as a user nobody is.
        AudienceRuleKind.User => request.UserId is { } userId && userId != Guid.Empty,

        _ => false,
    };

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
