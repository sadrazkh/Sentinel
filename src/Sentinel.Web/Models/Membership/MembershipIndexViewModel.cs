using Sentinel.Application.Access;
using Sentinel.Application.Memberships;

namespace Sentinel.Web.Models.Membership;

public sealed class MembershipIndexViewModel
{
    public required MembershipSnapshot Membership { get; init; }

    public required string TimeZoneId { get; init; }

    public required IReadOnlyList<ApplicationCard> UnlockedApplications { get; init; }

    public required IReadOnlyList<ApplicationCard> LockedApplications { get; init; }
}
