using Sentinel.Application.Common;
using Sentinel.Domain.Catalog;
using Sentinel.Domain.Memberships;

namespace Sentinel.Application.Catalog;

public sealed record ApplicationListItem(
    Guid Id,
    string Key,
    string NameFa,
    string NameEn,
    string? IconPath,
    ApplicationPublishStatus PublishStatus,
    bool IsEnabled,
    bool IsBeta,
    int DisplayOrder,
    bool RequiresExplicitEntitlement,
    MembershipTier? MinimumTier,
    int ActiveEntitlementCount,
    DateTimeOffset UpdatedAt);

public sealed record ApplicationEditModel(
    Guid Id,
    string Key,
    string NameFa,
    string NameEn,
    string? DescriptionFa,
    string? DescriptionEn,
    string? IconPath,
    string LaunchUrl,
    ApplicationPublishStatus PublishStatus,
    bool IsEnabled,
    bool IsBeta,
    int DisplayOrder,
    bool RequiresExplicitEntitlement,
    MembershipTier? MinimumTier,
    Guid ConcurrencyToken);

public sealed record ApplicationSaveRequest(
    string Key,
    string NameFa,
    string NameEn,
    string? DescriptionFa,
    string? DescriptionEn,
    string LaunchUrl,
    ApplicationPublishStatus PublishStatus,
    bool IsEnabled,
    bool IsBeta,
    int DisplayOrder,
    bool RequiresExplicitEntitlement,
    MembershipTier? MinimumTier,
    /// <summary><c>null</c> when creating.</summary>
    Guid? ConcurrencyToken);

public interface IApplicationAdminQuery
{
    Task<IReadOnlyList<ApplicationListItem>> ListAsync(CancellationToken cancellationToken = default);

    Task<ApplicationEditModel?> GetForEditAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Resolves the stored icon name for an application key, for the media endpoint.</summary>
    Task<string?> GetIconNameAsync(string applicationKey, CancellationToken cancellationToken = default);
}

public interface IApplicationAdminService
{
    Task<OperationResult<Guid>> CreateAsync(
        ApplicationSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> UpdateAsync(
        Guid id,
        ApplicationSaveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the icon. <paramref name="content"/> is validated by signature before anything
    /// touches disk, and the previous file is removed afterwards.
    /// </summary>
    Task<OperationResult> ReplaceIconAsync(
        Guid id,
        Stream content,
        long declaredLength,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RemoveIconAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Errors specific to the catalogue, on top of <see cref="OperationErrors"/>.</summary>
public static class CatalogErrors
{
    public const string KeyTaken = "admin.error.applicationKeyTaken";
    public const string InvalidKey = "admin.error.applicationKeyInvalid";
    public const string InvalidLaunchUrl = "admin.error.launchUrlInvalid";
    public const string InsecureLaunchUrl = "admin.error.launchUrlInsecure";
    public const string IconTooLarge = "admin.error.iconTooLarge";
    public const string IconNotAnImage = "admin.error.iconNotAnImage";
    public const string IconEmpty = "admin.error.iconEmpty";
}
