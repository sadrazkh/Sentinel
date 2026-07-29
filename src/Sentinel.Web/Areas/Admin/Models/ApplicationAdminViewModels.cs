using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Sentinel.Application.Catalog;
using Sentinel.Application.Entitlements;
using Sentinel.Domain.Catalog;
using Sentinel.Domain.Memberships;

namespace Sentinel.Web.Areas.Admin.Models;

public sealed class ApplicationListViewModel
{
    public required IReadOnlyList<ApplicationListItem> Applications { get; init; }

    public required bool CanWrite { get; init; }

    public required string TimeZoneId { get; init; }
}

public sealed class ApplicationEditViewModel
{
    /// <summary><see cref="Guid.Empty"/> while creating.</summary>
    public Guid Id { get; set; }

    [Required(ErrorMessage = "validation.required")]
    [StringLength(ApplicationKey.MaxLength, MinimumLength = ApplicationKey.MinLength,
        ErrorMessage = "validation.length")]
    [RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "admin.validation.applicationKey")]
    [Display(Name = "admin.application.key")]
    public string Key { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(PortalApplication.NameMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameFa")]
    public string NameFa { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(PortalApplication.NameMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameEn")]
    public string NameEn { get; set; } = string.Empty;

    [StringLength(PortalApplication.DescriptionMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.descriptionFa")]
    public string? DescriptionFa { get; set; }

    [StringLength(PortalApplication.DescriptionMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.descriptionEn")]
    public string? DescriptionEn { get; set; }

    /// <summary>
    /// Length and presence only. The scheme and host rules live in
    /// <see cref="ApplicationUrlPolicy"/>, which both this form and the launch endpoint use —
    /// duplicating them in an attribute would create a second, drift-prone copy.
    /// </summary>
    [Required(ErrorMessage = "validation.required")]
    [StringLength(ApplicationUrlPolicy.MaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.launchUrl")]
    public string LaunchUrl { get; set; } = string.Empty;

    [Display(Name = "admin.application.publishStatus")]
    public ApplicationPublishStatus PublishStatus { get; set; } = ApplicationPublishStatus.Draft;

    [Display(Name = "admin.application.isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [Display(Name = "admin.application.isBeta")]
    public bool IsBeta { get; set; }

    [Range(0, 10_000, ErrorMessage = "validation.range")]
    [Display(Name = "admin.application.displayOrder")]
    public int DisplayOrder { get; set; } = 100;

    [Display(Name = "admin.application.requiresEntitlement")]
    public bool RequiresExplicitEntitlement { get; set; }

    [Display(Name = "admin.application.minimumTier")]
    public MembershipTier? MinimumTier { get; set; }

    public Guid? ConcurrencyToken { get; set; }

    /// <summary>Stored file name, for rendering the current icon. Never posted back.</summary>
    public string? IconPath { get; set; }

    public bool IsNew => Id == Guid.Empty;

    public static ApplicationEditViewModel From(ApplicationEditModel model) => new()
    {
        Id = model.Id,
        Key = model.Key,
        NameFa = model.NameFa,
        NameEn = model.NameEn,
        DescriptionFa = model.DescriptionFa,
        DescriptionEn = model.DescriptionEn,
        LaunchUrl = model.LaunchUrl,
        PublishStatus = model.PublishStatus,
        IsEnabled = model.IsEnabled,
        IsBeta = model.IsBeta,
        DisplayOrder = model.DisplayOrder,
        RequiresExplicitEntitlement = model.RequiresExplicitEntitlement,
        MinimumTier = model.MinimumTier,
        ConcurrencyToken = model.ConcurrencyToken,
        IconPath = model.IconPath,
    };

    public ApplicationSaveRequest ToRequest() => new(
        Key,
        NameFa,
        NameEn,
        DescriptionFa,
        DescriptionEn,
        LaunchUrl,
        PublishStatus,
        IsEnabled,
        IsBeta,
        DisplayOrder,
        RequiresExplicitEntitlement,
        MinimumTier,
        ConcurrencyToken);
}

public sealed class UploadIconViewModel
{
    public Guid ApplicationId { get; set; }

    [Display(Name = "admin.application.icon")]
    public IFormFile? Icon { get; set; }
}

public sealed class GrantEntitlementViewModel
{
    public Guid UserId { get; set; }

    public Guid ApplicationId { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "admin.entitlement.starts")]
    public DateTime? StartsAt { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "admin.entitlement.expires")]
    public DateTime? ExpiresAt { get; set; }

    [StringLength(512, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.membership.notes")]
    public string? Notes { get; set; }

    public Guid? ConcurrencyToken { get; set; }
}

public sealed class UserEntitlementsViewModel
{
    public required Guid UserId { get; init; }

    public required string UserDisplayName { get; init; }

    public required IReadOnlyList<UserApplicationGrantRow> Rows { get; init; }

    public required bool CanWrite { get; init; }

    public required string TimeZoneId { get; init; }
}
