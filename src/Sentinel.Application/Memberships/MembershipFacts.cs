using Sentinel.Domain.Memberships;

namespace Sentinel.Application.Memberships;

/// <summary>
/// The only fields the status rules look at.
/// <para>
/// A flat record rather than the <see cref="Membership"/> entity, so a query can project
/// straight into it without loading the row, and so the rules can be unit-tested without
/// EF Core anywhere in sight.
/// </para>
/// </summary>
public sealed record MembershipFacts(
    MembershipTier Tier,
    MembershipAdminState AdminState,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    int? GracePeriodDaysOverride)
{
    public static MembershipFacts From(Membership membership) => new(
        membership.Tier,
        membership.AdminState,
        membership.StartsAt,
        membership.EndsAt,
        membership.GracePeriodDaysOverride);
}
