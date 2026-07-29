using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Access;
using Sentinel.Application.Accounts;
using Sentinel.Application.Authorization;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Models.Membership;

namespace Sentinel.Web.Controllers;

[Authorize(Policy = PolicyNames.ActiveUser)]
public sealed class MembershipController : Controller
{
    private readonly IAccessDecisionService _accessDecisions;
    private readonly IAccountOverviewQuery _accountOverview;

    public MembershipController(
        IAccessDecisionService accessDecisions,
        IAccountOverviewQuery accountOverview)
    {
        _accessDecisions = accessDecisions;
        _accountOverview = accountOverview;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // No id in the route: a member can only ever look at their own membership, so there is
        // nothing for a caller to substitute.
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Forbid();
        }

        var catalog = await _accessDecisions.GetCatalogAsync(userId, cancellationToken);
        var overview = await _accountOverview.GetAsync(userId, 1, cancellationToken);

        return View(new MembershipIndexViewModel
        {
            Membership = catalog.Membership,
            TimeZoneId = overview?.TimeZoneId ?? UserTime.DefaultTimeZoneId,
            UnlockedApplications = catalog.Applications.Where(a => a.CanLaunch).ToList(),
            LockedApplications = catalog.Applications.Where(a => !a.CanLaunch).ToList(),
        });
    }
}
