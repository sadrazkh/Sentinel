using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Sentinel.Application.Catalog;
using Sentinel.Application.Entitlements;
using Sentinel.Domain.Products;
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
    [StringLength(Product.NameMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameFa")]
    public string NameFa { get; set; } = string.Empty;

    [Required(ErrorMessage = "validation.required")]
    [StringLength(Product.NameMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.nameEn")]
    public string NameEn { get; set; } = string.Empty;

    [StringLength(Product.DescriptionMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.descriptionFa")]
    public string? DescriptionFa { get; set; }

    [StringLength(Product.DescriptionMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.descriptionEn")]
    public string? DescriptionEn { get; set; }

    /// <summary>
    /// Optional, and length-checked only. A download-only tool or a subscription service has
    /// nowhere to "open"; just the Launchable capability needs a destination.
    /// <para>
    /// The scheme and host rules live in <see cref="ApplicationUrlPolicy"/>, which both this
    /// form and the launch endpoint use — duplicating them in an attribute would create a
    /// second, drift-prone copy.
    /// </para>
    /// </summary>
    [StringLength(ApplicationUrlPolicy.MaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.application.launchUrl")]
    public string? LaunchUrl { get; set; }

    [Display(Name = "admin.product.type")]
    public ProductType Type { get; set; } = ProductType.WebApplication;

    /// <summary>
    /// Bound as a list of checked flags and recombined on save, so the form stays a set of
    /// plain checkboxes rather than asking an operator to reason about a bitmask.
    /// </summary>
    [Display(Name = "admin.product.capabilities")]
    public List<ProductCapability> SelectedCapabilities { get; set; } = [];

    public Guid? CategoryId { get; set; }

    [StringLength(Product.SummaryMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.product.summaryFa")]
    public string? SummaryFa { get; set; }

    [StringLength(Product.SummaryMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.product.summaryEn")]
    public string? SummaryEn { get; set; }

    [StringLength(Product.VersionMaxLength, ErrorMessage = "validation.tooLong")]
    [Display(Name = "admin.product.currentVersion")]
    public string? CurrentVersion { get; set; }

    [Display(Name = "admin.product.isFeatured")]
    public bool IsFeatured { get; set; }

    [Display(Name = "admin.application.releaseStatus")]
    public ProductReleaseStatus ReleaseStatus { get; set; } = ProductReleaseStatus.Draft;

    [Display(Name = "admin.application.isEnabled")]
    public bool IsEnabled { get; set; } = true;

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

    /// <summary>Offered as checkboxes, in a fixed order so the form is stable.</summary>
    public static readonly IReadOnlyList<ProductCapability> AllCapabilities =
        Enum.GetValues<ProductCapability>()
            .Where(capability => capability != ProductCapability.None)
            .ToList();

    public static ApplicationEditViewModel From(ApplicationEditModel model) => new()
    {
        Id = model.Id,
        Key = model.Key,
        NameFa = model.NameFa,
        NameEn = model.NameEn,
        SummaryFa = model.SummaryFa,
        SummaryEn = model.SummaryEn,
        DescriptionFa = model.DescriptionFa,
        DescriptionEn = model.DescriptionEn,
        LaunchUrl = model.LaunchUrl,
        Type = model.Type,
        SelectedCapabilities = AllCapabilities.Where(capability => model.Capabilities.Has(capability)).ToList(),
        CategoryId = model.CategoryId,
        CurrentVersion = model.CurrentVersion,
        IsFeatured = model.IsFeatured,
        ReleaseStatus = model.ReleaseStatus,
        IsEnabled = model.IsEnabled,
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
        SummaryFa,
        SummaryEn,
        DescriptionFa,
        DescriptionEn,
        LaunchUrl,
        Type,
        // Recombined from the checked boxes into the single bitmask the domain stores.
        SelectedCapabilities.Aggregate(ProductCapability.None, (all, one) => all | one),
        CategoryId,
        CurrentVersion,
        IsFeatured,
        ReleaseStatus,
        IsEnabled,
        DisplayOrder,
        RequiresExplicitEntitlement,
        MinimumTier,
        ConcurrencyToken);
}

public sealed class UploadIconViewModel
{
    public Guid ProductId { get; set; }

    [Display(Name = "admin.application.icon")]
    public IFormFile? Icon { get; set; }
}

public sealed class GrantEntitlementViewModel
{
    public Guid UserId { get; set; }

    public Guid ProductId { get; set; }

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
