namespace Sentinel.Domain.Identity;

/// <summary>
/// Lifecycle of the account itself. Deliberately separate from membership state:
/// an account can be perfectly healthy while its membership has expired, and an
/// account can be suspended while its membership is still paid up.
/// </summary>
public enum UserAccountStatus
{
    /// <summary>Normal account. May sign in and use entitled applications.</summary>
    Active = 1,

    /// <summary>Deactivated by an administrator. Sign-in is refused indefinitely.</summary>
    Disabled = 2,

    /// <summary>
    /// Blocked, optionally until <see cref="ApplicationUser.SuspendedUntil"/>.
    /// Used for abuse handling and security incidents.
    /// </summary>
    Suspended = 3,
}
