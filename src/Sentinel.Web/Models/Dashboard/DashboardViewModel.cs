using Sentinel.Application.Access;
using Sentinel.Application.Memberships;
using Sentinel.Application.Security;
using Sentinel.Domain.Identity;

namespace Sentinel.Web.Models.Dashboard;

public sealed class DashboardViewModel
{
    public required string DisplayName { get; init; }

    public required string UserName { get; init; }

    public string? Email { get; init; }

    public required UserAccountStatus Status { get; init; }

    public DateTimeOffset? SuspendedUntil { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastLoginAt { get; init; }

    public required string TimeZoneId { get; init; }

    public required int ActiveSessionCount { get; init; }

    public required IReadOnlyList<LoginAttemptView> RecentLoginAttempts { get; init; }

    /// <summary>Roles read from the signed-in principal; used for display and nothing else.</summary>
    public required IReadOnlyList<string> Roles { get; init; }

    /// <summary>Whether the admin area link is shown. The area itself re-checks on the server.</summary>
    public required bool CanAccessBackOffice { get; init; }

    public required MembershipSnapshot Membership { get; init; }

    /// <summary>The few applications highlighted on the dashboard; the full set lives on My Apps.</summary>
    public required IReadOnlyList<ApplicationCard> FeaturedApplications { get; init; }

    public required int AccessibleApplicationCount { get; init; }

    public required int LockedApplicationCount { get; init; }

    public required int ComingSoonApplicationCount { get; init; }
}
