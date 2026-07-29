using Sentinel.Domain.Common;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Memberships;

namespace Sentinel.Domain.Catalog;

/// <summary>
/// One product in the portal's catalogue. Named <c>PortalApplication</c> rather than
/// <c>Application</c> to avoid colliding with the framework types of that name.
/// </summary>
public class PortalApplication : IConcurrencyAware, ITimestamped
{
    public const int KeyMaxLength = 64;
    public const int NameMaxLength = 128;
    public const int DescriptionMaxLength = 1024;
    public const int IconPathMaxLength = 512;
    public const int LaunchUrlMaxLength = 2048;

    public Guid Id { get; set; }

    /// <summary>Stable slug used in URLs and configuration. Unique, lower-case, immutable in practice.</summary>
    public string Key { get; set; } = string.Empty;

    public string NameFa { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    public string? DescriptionFa { get; set; }

    public string? DescriptionEn { get; set; }

    /// <summary>Relative path of the uploaded icon, resolved by the icon endpoint. Never a raw client path.</summary>
    public string? IconPath { get; set; }

    /// <summary>
    /// Absolute https destination the member is sent to. Validated against
    /// <c>ApplicationUrlPolicy</c> before it is ever persisted.
    /// </summary>
    public string LaunchUrl { get; set; } = string.Empty;

    public ApplicationPublishStatus PublishStatus { get; set; } = ApplicationPublishStatus.Draft;

    /// <summary>Master switch. A disabled application cannot be launched by anyone, ever.</summary>
    public bool IsEnabled { get; set; } = true;

    public bool IsBeta { get; set; }

    public int DisplayOrder { get; set; }

    /// <summary>
    /// When <c>true</c>, a valid membership is not enough: the user needs an explicit
    /// <see cref="UserEntitlement"/>. When <c>false</c>, membership plus
    /// <see cref="MinimumTier"/> is sufficient.
    /// </summary>
    public bool RequiresExplicitEntitlement { get; set; }

    /// <summary>Lowest membership tier that may use this application. <c>null</c> means any tier.</summary>
    public MembershipTier? MinimumTier { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }

    public ICollection<UserEntitlement> Entitlements { get; set; } = new List<UserEntitlement>();
}
