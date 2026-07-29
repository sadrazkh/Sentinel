using Sentinel.Application.Access;
using Sentinel.Application.Memberships;

namespace Sentinel.Web.Models.Apps;

public sealed class AppsIndexViewModel
{
    public required MembershipSnapshot Membership { get; init; }

    public required IReadOnlyList<ApplicationCard> Applications { get; init; }

    public required string TimeZoneId { get; init; }

    public int AccessibleCount { get; init; }

    public int LockedCount { get; init; }
}

/// <summary>Shown, with a 403, when a launch is refused. Names the application and the reason, nothing else.</summary>
public sealed class LaunchDeniedViewModel
{
    public required string ApplicationName { get; init; }

    public required AccessDenialReason Reason { get; init; }
}
