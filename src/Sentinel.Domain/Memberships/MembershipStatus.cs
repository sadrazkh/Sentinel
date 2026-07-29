namespace Sentinel.Domain.Memberships;

/// <summary>
/// Effective membership status, always computed — never persisted.
/// </summary>
public enum MembershipStatus
{
    /// <summary>The user has no membership record at all.</summary>
    None = 0,

    /// <summary>Membership exists but its start date is in the future, or it awaits approval.</summary>
    Pending = 1,

    /// <summary>Inside the paid window.</summary>
    Active = 2,

    /// <summary>Past the end date but still inside the configured grace period.</summary>
    GracePeriod = 3,

    /// <summary>Past the end date and past the grace period.</summary>
    Expired = 4,

    /// <summary>Halted by an administrator.</summary>
    Suspended = 5,

    /// <summary>Terminated by the user or an administrator.</summary>
    Cancelled = 6,
}

public static class MembershipStatusExtensions
{
    /// <summary>
    /// Statuses that still entitle the member to launch applications.
    /// Grace period counts as valid on purpose: it exists so a late renewal does not
    /// lock a paying customer out mid-work.
    /// </summary>
    public static bool GrantsAccess(this MembershipStatus status) =>
        status is MembershipStatus.Active or MembershipStatus.GracePeriod;
}
