using Sentinel.Application.Common;
using Sentinel.Domain.Memberships;
using Sentinel.Vpn.Domain;

namespace Sentinel.Vpn.Plans;

/// <summary>
/// One plan as a member sees it.
/// <para>
/// Read-only, and deliberately so: it carries no identifier a purchase flow could use because
/// purchasing does not exist yet. When it does, the order will be placed by plan id and the terms
/// read from the row — never from anything the browser sends back.
/// </para>
/// </summary>
public sealed record ServicePlanCard(
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
    /// <summary>
    /// Whether an order could be placed — the plan's own switch <em>and</em> the purchase feature.
    /// False today for every plan, because the feature ships off.
    /// </summary>
    bool CanOrder)
{
    public bool IsUnlimitedTraffic => TrafficBytes <= 0;

    public bool IsUnlimitedDevices => DeviceLimit <= 0;

    public bool IsFree => PriceMinorUnits <= 0;
}

/// <summary>The plan list for one product and one member, with the audience already applied.</summary>
public sealed record ServicePlanCatalog(
    IReadOnlyList<ServicePlanCard> Plans,
    /// <summary>
    /// Countries a plan could actually be delivered in right now — the intersection of the plans on
    /// offer and the servers that are healthy and have room. Shown so a member is not offered a
    /// location the portal cannot currently provision.
    /// </summary>
    IReadOnlyList<string> AvailableCountries,
    bool PurchasingEnabled)
{
    public static readonly ServicePlanCatalog Empty = new([], [], false);

    public bool HasPlans => Plans.Count > 0;
}

/// <summary>
/// A plan as the operator edits it. Carries the audience rules, which a member's view never does.
/// </summary>
public sealed record ServicePlanEditModel(
    Guid Id,
    string Key,
    Guid ProductId,
    string NameFa,
    string NameEn,
    string? DescriptionFa,
    string? DescriptionEn,
    long TrafficBytes,
    int DurationDays,
    int DeviceLimit,
    long PriceMinorUnits,
    string Currency,
    bool IsVisible,
    bool IsPurchasable,
    string? CountryCode,
    int DisplayOrder,
    bool IsFeatured,
    IReadOnlyList<AudienceRuleRow> AudienceRules,
    Guid? ConcurrencyToken);

public sealed record AudienceRuleRow(
    Guid Id,
    AudienceEffect Effect,
    AudienceRuleKind Kind,
    MembershipTier? Tier,
    string? RoleName,
    Guid? UserId,
    string? Note);

public sealed record ServicePlanSaveRequest(
    string Key,
    Guid ProductId,
    string NameFa,
    string NameEn,
    string? DescriptionFa,
    string? DescriptionEn,
    long TrafficBytes,
    int DurationDays,
    int DeviceLimit,
    long PriceMinorUnits,
    string Currency,
    bool IsVisible,
    bool IsPurchasable,
    string? CountryCode,
    int DisplayOrder,
    bool IsFeatured,
    Guid? ConcurrencyToken);

public sealed record AudienceRuleSaveRequest(
    AudienceEffect Effect,
    AudienceRuleKind Kind,
    MembershipTier? Tier,
    string? RoleName,
    Guid? UserId,
    string? Note);

/// <summary>A plan row for the operator's list, with the counts they need to spot a mistake.</summary>
public sealed record ServicePlanListItem(
    Guid Id,
    string Key,
    Guid ProductId,
    string ProductNameFa,
    string ProductNameEn,
    string NameFa,
    string NameEn,
    long TrafficBytes,
    int DurationDays,
    int DeviceLimit,
    long PriceMinorUnits,
    string Currency,
    bool IsVisible,
    bool IsPurchasable,
    string? CountryCode,
    int DisplayOrder,
    bool IsFeatured,
    int AllowRuleCount,
    int DenyRuleCount)
{
    /// <summary>
    /// A plan restricted to an audience nobody can be in. Worth flagging: it renders for no member
    /// at all, which looks identical to a plan that was never created.
    /// </summary>
    public bool HasAudience => AllowRuleCount > 0 || DenyRuleCount > 0;
}

public static class PlanErrors
{
    public const string NotFound = "admin.error.planNotFound";
    public const string KeyTaken = "admin.error.planKeyTaken";
    public const string KeyInvalid = "admin.error.planKeyInvalid";
    public const string ProductNotFound = "admin.error.planProductNotFound";
    public const string CurrencyInvalid = "admin.error.planCurrencyInvalid";
    public const string CountryInvalid = "admin.error.planCountryInvalid";
    public const string DurationInvalid = "admin.error.planDurationInvalid";
    public const string NegativeAmount = "admin.error.planNegativeAmount";
    public const string RuleIncomplete = "admin.error.planRuleIncomplete";
}

/// <summary>
/// The plans a member is offered.
/// <para>
/// The audience decision is made here, once, and the member-facing projection carries no trace of
/// it — a plan withheld from somebody looks to them exactly like a plan that does not exist.
/// </para>
/// </summary>
public interface IServicePlanCatalog
{
    Task<ServicePlanCatalog> GetForMemberAsync(
        Guid userId,
        Guid productId,
        CancellationToken cancellationToken = default);
}

public interface IServicePlanAdminService
{
    Task<OperationResult<Guid>> SaveAsync(
        Guid? planId,
        ServicePlanSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteAsync(Guid planId, CancellationToken cancellationToken = default);

    Task<OperationResult> AddRuleAsync(
        Guid planId,
        AudienceRuleSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RemoveRuleAsync(Guid ruleId, CancellationToken cancellationToken = default);
}

public interface IServicePlanAdminQuery
{
    Task<IReadOnlyList<ServicePlanListItem>> ListAsync(CancellationToken cancellationToken = default);

    Task<ServicePlanEditModel?> GetForEditAsync(Guid planId, CancellationToken cancellationToken = default);
}
