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
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireFeatureAttribute : Attribute, IActionFilter
{
    private readonly string _featureName;

    public RequireFeatureAttribute(string featureName) => _featureName = featureName;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var gate = context.HttpContext.RequestServices.GetRequiredService<IFeatureGate>();

        if (!gate.IsEnabled(_featureName))
        {
            context.Result = new NotFoundResult();
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
