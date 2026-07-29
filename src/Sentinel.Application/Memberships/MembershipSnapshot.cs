using Sentinel.Domain.Memberships;

namespace Sentinel.Application.Memberships;

/// <summary>
/// The computed view of a membership at one instant: what every screen and every access
/// check reads instead of re-deriving expiry from raw dates.
/// </summary>
public sealed record MembershipSnapshot(
    MembershipStatus Status,
    MembershipTier? Tier,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    /// <summary>When access actually stops, i.e. <see cref="EndsAt"/> plus any grace period.</summary>
    DateTimeOffset? AccessEndsAt,
    /// <summary>
    /// Whole days until access stops, rounded up so a membership with any time left never
    /// reads as "0 days". <c>null</c> for open-ended memberships and for statuses that
    /// already grant nothing.
    /// </summary>
    int? DaysRemaining,
    bool IsRenewalDueSoon)
{
    /// <summary>No membership record at all.</summary>
    public static readonly MembershipSnapshot None =
        new(MembershipStatus.None, null, null, null, null, null, false);

    public bool GrantsAccess => Status.GrantsAccess();

    /// <summary>True while inside the post-expiry grace window.</summary>
    public bool IsInGracePeriod => Status == MembershipStatus.GracePeriod;
}
