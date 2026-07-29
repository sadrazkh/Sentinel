namespace Sentinel.Domain.Memberships;

/// <summary>
/// What an administrator explicitly set. This is stored; it is <em>not</em> the effective
/// status — expiry and grace period are derived from dates by
/// <c>IMembershipStatusResolver</c> so the two can never drift apart in the database.
/// </summary>
public enum MembershipAdminState
{
    /// <summary>Created but not yet started, or awaiting payment/approval.</summary>
    Pending = 1,

    /// <summary>Normal state. Effective status is then decided by the date window.</summary>
    Active = 2,

    /// <summary>Temporarily halted by an administrator regardless of dates.</summary>
    Suspended = 3,

    /// <summary>Terminated and will not be renewed.</summary>
    Cancelled = 4,
}
