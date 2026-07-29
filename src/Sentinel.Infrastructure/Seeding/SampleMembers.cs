using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;

namespace Sentinel.Infrastructure.Seeding;

/// <summary>
/// The development fixtures. Each one exists to make a different state of the portal reachable
/// without hand-editing the database, which is what makes the access rules inspectable.
/// </summary>
internal sealed record SampleMember(
    string UserName,
    string DisplayName,
    string? PhoneNumber,
    UserAccountStatus AccountStatus,
    bool WithMembership,
    MembershipTier Tier,
    MembershipAdminState MembershipState,
    int? EndsInDays,
    string Purpose);

internal static class SampleMembers
{
    public static readonly IReadOnlyList<SampleMember> All =
    [
        new("member.active", "کاربر فعال", "+989120000001",
            UserAccountStatus.Active, true, MembershipTier.Pro, MembershipAdminState.Active, 45,
            "Healthy Pro member: everything at or below the Pro tier is open."),

        new("member.basic", "کاربر پایه", "+989120000002",
            UserAccountStatus.Active, true, MembershipTier.Basic, MembershipAdminState.Active, 90,
            "Basic tier: shows a card locked by MinimumTier rather than by expiry."),

        new("member.expiring", "کاربر نزدیک به انقضا", "+989120000003",
            UserAccountStatus.Active, true, MembershipTier.Pro, MembershipAdminState.Active, 2,
            "Two days left: raises the renewal warning on the dashboard."),

        new("member.grace", "کاربر در مهلت تمدید", "+989120000004",
            UserAccountStatus.Active, true, MembershipTier.Pro, MembershipAdminState.Active, -1,
            "Ended yesterday: inside the grace period, so access continues."),

        new("member.expired", "کاربر منقضی", "+989120000005",
            UserAccountStatus.Active, true, MembershipTier.Pro, MembershipAdminState.Active, -30,
            "Well past the grace period: every membership-based card is locked."),

        new("member.suspended", "کاربر مسدود", "+989120000006",
            UserAccountStatus.Suspended, true, MembershipTier.Pro, MembershipAdminState.Active, 60,
            "Valid membership but a suspended account: sign-in itself is refused."),

        new("member.nomembership", "کاربر بدون عضویت", "+989120000007",
            UserAccountStatus.Active, false, MembershipTier.Basic, MembershipAdminState.Pending, null,
            "No membership row at all: the empty state on the membership page."),
    ];
}
