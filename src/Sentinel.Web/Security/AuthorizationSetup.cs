using Microsoft.AspNetCore.Authorization;
using Sentinel.Application.Authorization;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Security;

namespace Sentinel.Web.Security;

public static class AuthorizationSetup
{
    public static IServiceCollection AddSentinelAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            // Nothing is reachable without an explicit policy: any endpoint that forgets
            // [Authorize] still lands here rather than silently becoming public.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build())

            .AddPolicy(PolicyNames.ActiveUser, policy => policy
                .RequireAuthenticatedUser()
                // Account status (disabled/suspended) and session revocation are enforced on
                // every request by SessionValidationCookieEvents; the claim's presence proves
                // the cookie went through that path.
                .RequireClaim(UserSession.ClaimType))

            .AddPolicy(PolicyNames.BackOfficeRead, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(UserSession.ClaimType)
                .RequireRole(RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Support))

            .AddPolicy(PolicyNames.BackOfficeWrite, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(UserSession.ClaimType)
                // Support is deliberately excluded: it is a read-only troubleshooting role.
                .RequireRole(RoleNames.SuperAdmin, RoleNames.Admin))

            .AddPolicy(PolicyNames.SystemAdministration, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(UserSession.ClaimType)
                .RequireRole(RoleNames.SuperAdmin));

        return services;
    }
}
