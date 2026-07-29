namespace Sentinel.Application.Access;

/// <summary>
/// Why access was refused. Reported to the member as a friendly, non-technical line — a
/// locked card should say "your membership has expired", not leak whether some other user
/// holds a grant for the same application.
/// </summary>
public enum AccessDenialReason
{
    None = 0,

    AccountDisabled = 1,
    AccountSuspended = 2,

    ApplicationDisabled = 10,
    ApplicationNotPublished = 11,
    ApplicationComingSoon = 12,
    ApplicationRetired = 13,

    MembershipInvalid = 20,
    TierTooLow = 21,

    NoEntitlement = 30,
    EntitlementDisabled = 31,
    EntitlementRevoked = 32,
    EntitlementNotStarted = 33,
    EntitlementExpired = 34,
}

public sealed record AccessDecision(bool IsAllowed, AccessDenialReason Reason)
{
    public static readonly AccessDecision Allowed = new(true, AccessDenialReason.None);

    public static AccessDecision Denied(AccessDenialReason reason) => new(false, reason);

    /// <summary>
    /// True when the application should still be listed, just not launchable — a "coming soon"
    /// teaser, or something the member could unlock by renewing.
    /// </summary>
    public bool IsVisibleButLocked => !IsAllowed && Reason is not (
        AccessDenialReason.ApplicationDisabled or
        AccessDenialReason.ApplicationNotPublished or
        AccessDenialReason.AccountDisabled or
        AccessDenialReason.AccountSuspended);
}
