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

    /// <summary>Revokes every session for the current user and invalidates existing cookies.</summary>
    Task SignOutEverywhereAsync(CancellationToken cancellationToken = default);
}
