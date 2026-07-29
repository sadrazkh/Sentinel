using Sentinel.Domain.Security;

namespace Sentinel.Web.Services;

public sealed record SignInRequest(string Identifier, string Password, bool RememberMe);

/// <summary>
/// The reason is for logs and audit only. Every failure is rendered to the client as one
/// identical message, so the form cannot be used to discover which accounts exist.
/// </summary>
public sealed record SignInOutcome(bool Succeeded, LoginFailureReason Reason)
{
    public static readonly SignInOutcome Success = new(true, LoginFailureReason.None);

    public static SignInOutcome Failed(LoginFailureReason reason) => new(false, reason);
}

public interface IPortalSignInService
{
    Task<SignInOutcome> SignInAsync(SignInRequest request, CancellationToken cancellationToken = default);

    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-issues the authentication cookie for the current user, keeping the same server-side
    /// session.
    /// <para>
    /// Needed after anything that rotates the security stamp (a password change) or changes a
    /// claim the cookie carries (a display-name edit). Identity's own
    /// <c>RefreshSignInAsync</c> cannot be used on its own: it rebuilds the principal from the
    /// user and therefore drops the session-id claim, which the per-request validator requires
    /// — the effect would be silently signing the member out for saving their own profile.
    /// </para>
    /// </summary>
    Task RefreshSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>Revokes every session for the current user and invalidates existing cookies.</summary>
    Task SignOutEverywhereAsync(CancellationToken cancellationToken = default);
}
