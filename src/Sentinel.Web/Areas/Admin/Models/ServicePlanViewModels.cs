using System.ComponentModel.DataAnnotations;
using Sentinel.Domain.Memberships;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Plans;

namespace Sentinel.Web.Areas.Admin.Models;

public sealed class ServicePlanListViewModel
{
    public required IReadOnlyList<ServicePlanListItem> Plans { get; init; }

    public required bool CanWrite { get; init; }
}

public sealed class ServicePlanEditViewModel
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "validation.required")]
    [StringLength(ServicePlan.KeyMaxLength, MinimumLength = 2, ErrorMessage = "validation.length")]
    [RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "admin.validation.applicationKey")]
    [Display(Name = "admin.plan.key")]
    public string Key { get; set; } = string.Empty;

    [Display(Name = "admin.plan.product")]
    public Guid ProductId { get; set; }

    [Required(ErrorMessage = "validation.required")]
    [StringLength(ServicePlan.NameMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameFa")]
    public string NameFa { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(ServicePlan.NameMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameEn")]
    public string NameEn { get; set; } = string.Empty;

    [StringLength(ServicePlan.DescriptionMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.descriptionFa")]
    public string? DescriptionFa { get; set; }

    [StringLength(ServicePlan.DescriptionMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.descriptionEn")]
    public string? DescriptionEn { get; set; }

    /// <summary>
    /// Bytes, not gigabytes. Entered raw because the value goes to the panel unchanged and a
    /// friendlier unit here would mean a conversion that could silently be off by 1000/1024.
    /// </summary>
    [Range(0, long.MaxValue, ErrorMessage = "validation.range")]
    [Display(Name = "admin.plan.traffic")]
    public long TrafficBytes { get; set; }

    [Range(1, 3650, ErrorMessage = "admin.error.planDurationInvalid")]
    [Display(Name = "admin.plan.duration")]
    public int DurationDays { get; set; } = 30;

    [Range(0, 1000, ErrorMessage = "validation.range")]
    [Display(Name = "admin.plan.devices")]
    public int DeviceLimit { get; set; } = 2;

    [Range(0, long.MaxValue, ErrorMessage = "validation.range")]
    [Display(Name = "admin.plan.price")]
    public long PriceMinorUnits { get; set; }

    [Required(ErrorMessage = "validation.required")]
    [StringLength(3, MinimumLength = 3, ErrorMessage = "validation.length")]
    [RegularExpression("^[A-Za-z]{3}$", ErrorMessage = "admin.error.planCurrencyInvalid")]
    [Display(Name = "admin.plan.currency")]
    public string Currency { get; set; } = "IRR";

    [StringLength(2, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.plan.country")]
    public string? CountryCode { get; set; }

    [Display(Name = "admin.plan.visible")]
    public bool IsVisible { get; set; } = true;

    [Display(Name = "admin.plan.purchasable")]
    public bool IsPurchasable { get; set; }

    [Range(0, 10_000, ErrorMessage = "validation.range")]
    [Display(Name = "admin.application.displayOrder")]
    public int DisplayOrder { get; set; } = 100;

    [Display(Name = "admin.plan.featured")]
    public bool IsFeatured { get; set; }

    public Guid? ConcurrencyToken { get; set; }

    public bool IsNew => Id == Guid.Empty;

    /// <summary>Read-only on this form; rules are added and removed one at a time.</summary>
    public IReadOnlyList<AudienceRuleRow> AudienceRules { get; set; } = [];

    public static ServicePlanEditViewModel From(ServicePlanEditModel model) => new()
    {
        Id = model.Id,
        Key = model.Key,
        ProductId = model.ProductId,
        NameFa = model.NameFa,
        NameEn = model.NameEn,
        DescriptionFa = model.DescriptionFa,
        DescriptionEn = model.DescriptionEn,
        TrafficBytes = model.TrafficBytes,
        DurationDays = model.DurationDays,
        DeviceLimit = model.DeviceLimit,
        PriceMinorUnits = model.PriceMinorUnits,
        Currency = model.Currency,
        CountryCode = model.CountryCode,
        IsVisible = model.IsVisible,
        IsPurchasable = model.IsPurchasable,
        DisplayOrder = model.DisplayOrder,
        IsFeatured = model.IsFeatured,
        AudienceRules = model.AudienceRules,
        ConcurrencyToken = model.ConcurrencyToken,
    };

    public ServicePlanSaveRequest ToRequest() => new(
        Key,
        ProductId,
        NameFa,
        NameEn,
        DescriptionFa,
        DescriptionEn,
        TrafficBytes,
        DurationDays,
        DeviceLimit,
        PriceMinorUnits,
        Currency,
        IsVisible,
        IsPurchasable,
        CountryCode,
        DisplayOrder,
        IsFeatured,
        ConcurrencyToken);
}

/// <summary>One audience rule as the operator adds it.</summary>
public sealed class AudienceRuleInputModel
{
    [Display(Name = "admin.plan.audience.effect")]
    public AudienceEffect Effect { get; set; } = AudienceEffect.Allow;

    [Display(Name = "admin.plan.audience.kind")]
    public AudienceRuleKind Kind { get; set; } = AudienceRuleKind.Everyone;

    [Display(Name = "admin.plan.audience.tier")]
    public MembershipTier? Tier { get; set; }

    [StringLength(PlanAudienceRule.RoleNameMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.plan.audience.role")]
    public string? RoleName { get; set; }

    [Display(Name = "admin.plan.audience.user")]
    public Guid? UserId { get; set; }

    [StringLength(PlanAudienceRule.NoteMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.plan.audience.note")]
    public string? Note { get; set; }

    public AudienceRuleSaveRequest ToRequest() =>
        new(Effect, Kind, Tier, RoleName, UserId, Note);
}
