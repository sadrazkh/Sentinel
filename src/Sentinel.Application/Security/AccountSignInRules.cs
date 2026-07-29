using Sentinel.Domain.Identity;

namespace Sentinel.Application.Security;

/// <summary>
/// Whether an account is in a state that permits signing in at all — evaluated before any
/// password check and again on every request that presents a cookie.
/// <para>
/// A pure function on purpose: it is the rule that decides whether a disabled or suspended
/// customer can reach the portal, so it is unit-tested directly instead of only through a
/// running web host.
/// </para>
/// </summary>
public static class AccountSignInRules
{
    public static bool CanSignIn(ApplicationUser user, DateTimeOffset now) =>
        user.Status switch
        {
            UserAccountStatus.Active => true,

            UserAccountStatus.Disabled => false,

            // A timed suspension lapses on its own once the deadline passes; an open-ended
            // one (SuspendedUntil is null) needs an administrator to lift it.
            UserAccountStatus.Suspended => user.SuspendedUntil is { } until && until <= now,

            // Unknown status: refuse. A new state must be classified deliberately, and
            // defaulting to "allowed" would turn a missed case into an access hole.
            _ => false,
        };
}
