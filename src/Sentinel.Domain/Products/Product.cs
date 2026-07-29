using Sentinel.Domain.Common;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Memberships;

namespace Sentinel.Domain.Products;

/// <summary>
/// One item in the product library — a VPN service, a web tool, a downloadable app, a game.
/// <para>
/// This table holds only what every product has. Nothing VPN-specific belongs here: servers,
/// quotas and inbound profiles live in the VPN module, keyed by <see cref="Id"/>. That
/// separation is the whole point of the model — a second product with entirely different
/// mechanics must not require a column here, and VPN's rules must not leak into the catalogue
/// every other product shares.
/// </para>
/// </summary>
public class Product : IConcurrencyAware, ITimestamped
{
    public const int KeyMaxLength = 64;
    public const int NameMaxLength = 128;
    public const int SummaryMaxLength = 300;
    public const int DescriptionMaxLength = 4000;
    public const int MediaPathMaxLength = 512;
    public const int LaunchUrlMaxLength = 2048;
    public const int VersionMaxLength = 32;

    public Guid Id { get; set; }

    /// <summary>Stable slug used in URLs and configuration. Unique, lower-case, immutable in practice.</summary>
    public string Key { get; set; } = string.Empty;

    public Guid? CategoryId { get; set; }

    public ProductCategory? Category { get; set; }

    public ProductType Type { get; set; } = ProductType.WebApplication;

    /// <summary>
    /// What this product supports. Everything that varies between products is expressed here
    /// rather than through branches on <see cref="Type"/>.
    /// </summary>
    public ProductCapability Capabilities { get; set; } = ProductCapability.None;

    public ProductReleaseStatus ReleaseStatus { get; set; } = ProductReleaseStatus.Draft;

    /// <summary>Master switch, independent of release status. A disabled product is closed to everyone.</summary>
    public bool IsEnabled { get; set; } = true;

    // ---- presentation ------------------------------------------------------------------

    public string NameFa { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    /// <summary>One line for the card. Longer prose belongs in a product section.</summary>
    public string? SummaryFa { get; set; }

    public string? SummaryEn { get; set; }

    public string? DescriptionFa { get; set; }

    public string? DescriptionEn { get; set; }

    /// <summary>Stored icon file name, served through the media endpoint.</summary>
    public string? IconPath { get; set; }

    /// <summary>Wide image for the details page hero.</summary>
    public string? CoverPath { get; set; }

    public string? CurrentVersion { get; set; }

    public int DisplayOrder { get; set; }

    /// <summary>Pinned to the top of the catalogue.</summary>
    public bool IsFeatured { get; set; }

    // ---- access ------------------------------------------------------------------------

    /// <summary>
    /// Where a launchable product opens. Validated against the URL policy before it is stored
    /// and again before any redirect.
    /// </summary>
    public string? LaunchUrl { get; set; }

    /// <summary>
    /// When <c>true</c>, a valid membership is not enough: the member needs an explicit
    /// entitlement. When <c>false</c>, membership plus <see cref="MinimumTier"/> is sufficient.
    /// </summary>
    public bool RequiresExplicitEntitlement { get; set; }

    /// <summary>Lowest membership tier that may use this product. <c>null</c> means any tier.</summary>
    public MembershipTier? MinimumTier { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }

    public ICollection<ProductEntitlement> Entitlements { get; set; } = new List<ProductEntitlement>();

    // ---- derived -----------------------------------------------------------------------

    /// <summary>
    /// Whether the catalogue may show this product at all, before any per-member reasoning.
    /// Draft and Archived are internal states and never surface.
    /// </summary>
    public bool IsListable =>
        IsEnabled
        && ReleaseStatus is not (ProductReleaseStatus.Draft or ProductReleaseStatus.Archived);

    /// <summary>Release stages that need an invitation rather than merely a membership.</summary>
    public bool IsInviteOnly =>
        ReleaseStatus is ProductReleaseStatus.PrivatePreview or ProductReleaseStatus.Alpha
        || Capabilities.Has(ProductCapability.BetaAccess);
}
