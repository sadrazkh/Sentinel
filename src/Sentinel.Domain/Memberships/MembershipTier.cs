namespace Sentinel.Domain.Memberships;

/// <summary>
/// Ordered on purpose: an application may declare a <c>MinimumTier</c>, and the comparison
/// is a plain numeric one. Add new tiers with values that preserve the ordering.
/// </summary>
public enum MembershipTier
{
    Basic = 1,
    Pro = 2,
    Elite = 3,
}
