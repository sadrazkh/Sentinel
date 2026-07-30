using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Sentinel.Application.Features;

namespace Sentinel.Web.Security;

/// <summary>
/// Refuses an endpoint whose feature is switched off.
/// <para>
/// Answers 404 rather than 403 on purpose: a disabled feature should be indistinguishable from
/// one that was never built. A 403 would confirm the endpoint exists and merely is not for you,
/// which is a map of the system's unreleased surface.
/// </para>
/// <para>
/// This is the enforcement half of a feature flag. Hiding the navigation link is presentation;
/// without this the endpoint stays reachable by anyone who types the URL.
/// </para>
/// </summary>
/// <remarks>
/// An authorization filter rather than an action filter, and ordered ahead of the rest.
/// <para>
/// The distinction matters for the claim this attribute makes. As an action filter it ran
/// <em>after</em> anti-forgery validation, so a POST to a switched-off feature answered 400 — which
/// says "this endpoint exists and your token was wrong" and tells an outsider precisely what the
/// 404 was meant to hide. Running first means the endpoint is gone before anything else looks at
/// the request.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireFeatureAttribute : Attribute, IAuthorizationFilter, IOrderedFilter
{
    private readonly string _featureName;

    public RequireFeatureAttribute(string featureName) => _featureName = featureName;

    /// <summary>Before authentication, authorization and anti-forgery. Nothing precedes this.</summary>
    public int Order => int.MinValue;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var gate = context.HttpContext.RequestServices.GetRequiredService<IFeatureGate>();

        if (!gate.IsEnabled(_featureName))
        {
            context.Result = new NotFoundResult();
        }
    }
}
