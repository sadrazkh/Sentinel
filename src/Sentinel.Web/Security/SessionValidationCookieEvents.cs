using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Sentinel.Application.Security;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Security;

namespace Sentinel.Web.Security;

/// <summary>
/// Runs on every authenticated request and rejects a cookie whose server-side session has
/// been revoked, or whose account is no longer allowed to sign in.
/// <para>
/// Without this, "log out" would only delete the client's copy of the cookie: a cookie
/// captured beforehand would keep working until it expired. Identity's own security-stamp
/// check runs on a timer (30 minutes by default) and is therefore not enough on its own.
/// </para>
/// </summary>
public sealed class SessionValidationCookieEvents : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        // Keep Identity's security-stamp validation: it is what invalidates cookies after a
        // password change or a role edit.
        await SecurityStampValidator.ValidatePrincipalAsync(context);

        if (context.Principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var services = context.HttpContext.RequestServices;

        var sessionClaim = context.Principal.FindFirstValue(UserSession.ClaimType);
        if (!Guid.TryParse(sessionClaim, out var sessionId))
        {
            // A cookie issued before sessions existed, or a tampered one.
            await RejectAsync(context);
            return;
        }

        var sessions = services.GetRequiredService<IUserSessionService>();
        var cancellationToken = context.HttpContext.RequestAborted;

        if (!await sessions.IsActiveAsync(sessionId, cancellationToken))
        {
            await RejectAsync(context);
            return;
        }

        var userIdClaim = context.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            await RejectAsync(context);
            return;
        }

        // The account may have been disabled or suspended while this cookie was alive.
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString());

        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        if (user is null || !AccountSignInRules.CanSignIn(user, now))
        {
            await sessions.RevokeAsync(sessionId, SessionRevocationReason.AccountDisabled, cancellationToken);
            await RejectAsync(context);
            return;
        }

        await sessions.TouchAsync(sessionId, cancellationToken);
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }
}
