namespace Sentinel.Domain.Security;

/// <summary>
/// Recorded for security monitoring only. The reason is deliberately never surfaced to the
/// client: every failed sign-in renders one identical message so the login form cannot be
/// used to enumerate which usernames or e-mail addresses exist.
/// </summary>
public enum LoginFailureReason
{
    None = 0,
    UnknownUser = 1,
    InvalidPassword = 2,
    LockedOut = 3,
    AccountDisabled = 4,
    AccountSuspended = 5,
    NotAllowed = 6,
    RateLimited = 7,
}
