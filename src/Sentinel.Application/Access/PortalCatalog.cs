using Sentinel.Application.Memberships;
using Sentinel.Domain.Catalog;
using Sentinel.Domain.Memberships;

namespace Sentinel.Application.Access;

/// <summary>
/// One application as the member sees it.
/// <para>
/// Deliberately carries no launch URL. The client never receives the destination — opening an
/// application always goes through the portal's own launch endpoint, which re-checks access
/// server-side and records the launch. Shipping the URL to the browser would make the visible
/// lock a decoration rather than a control.
/// </para>
/// </summary>
public sealed record ApplicationCard(
    Guid Id,
    string Key,
    string NameFa,
    string NameEn,
    string? DescriptionFa,
    string? DescriptionEn,
    string? IconPath,
    bool IsBeta,
    ApplicationPublishStatus PublishStatus,
    int DisplayOrder,
    MembershipTier? MinimumTier,
    AccessDecision Decision)
{
    public bool CanLaunch => Decision.IsAllowed;
}

/// <summary>Everything the dashboard and the applications page need, from one read.</summary>
public sealed record PortalCatalog(
    MembershipSnapshot Membership,
    IReadOnlyList<ApplicationCard> Applications)
{
    public int AccessibleCount => Applications.Count(a => a.CanLaunch);

    public int LockedCount => Applications.Count(a => !a.CanLaunch);

    public int BetaCount => Applications.Count(a => a.IsBeta && a.CanLaunch);

    public int ComingSoonCount =>
        Applications.Count(a => a.PublishStatus == ApplicationPublishStatus.ComingSoon);
}

/// <summary>
/// The outcome of a launch request. <see cref="LaunchUrl"/> is populated only when
/// <see cref="Decision"/> allows it, so a refused launch cannot leak the destination.
/// </summary>
public sealed record LaunchResolution(
    Guid ApplicationId,
    string ApplicationKey,
    string ApplicationName,
    AccessDecision Decision,
    string? LaunchUrl);
