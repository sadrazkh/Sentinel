using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Access;
using Sentinel.Application.Accounts;
using Sentinel.Application.Authorization;
using Sentinel.Domain.Identity;
using Sentinel.Web.Models.Dashboard;

namespace Sentinel.Web.Controllers;

[Authorize(Policy = PolicyNames.ActiveUser)]
public sealed class DashboardController : Controller
{
    private const int RecentLoginCount = 5;

    /// <summary>How many application cards the dashboard shows before deferring to My Apps.</summary>
    private const int FeaturedApplicationCount = 4;

    private readonly IAccountOverviewQuery _accountOverview;
    private readonly IAccessDecisionService _accessDecisions;

    public DashboardController(
        IAccountOverviewQuery accountOverview,
        IAccessDecisionService accessDecisions)
    {
        _accountOverview = accountOverview;
        _accessDecisions = accessDecisions;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // The id comes from the authenticated principal, never from the request. There is no
        // user id in the route precisely so that there is nothing for a caller to tamper with.
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Forbid();
        }

        var overview = await _accountOverview.GetAsync(userId, RecentLoginCount, cancellationToken);

        if (overview is null)
        {
            // Authenticated against an account that no longer exists.
            return Forbid();
        }

        var catalog = await _accessDecisions.GetCatalogAsync(userId, cancellationToken);
        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

        // Launchable applications first, so the dashboard leads with what the member can
        // actually use rather than with what they cannot.
        var featured = catalog.Applications
            .OrderByDescending(a => a.CanLaunch)
            .ThenBy(a => a.DisplayOrder)
            .Take(FeaturedApplicationCount)
            .ToList();

        return View(new DashboardViewModel
        {
            DisplayName = overview.DisplayName,
            UserName = overview.UserName,
            Email = overview.Email,
            Status = overview.Status,
            SuspendedUntil = overview.SuspendedUntil,
            CreatedAt = overview.CreatedAt,
            LastLoginAt = overview.LastLoginAt,
            TimeZoneId = overview.TimeZoneId,
            ActiveSessionCount = overview.ActiveSessionCount,
            RecentLoginAttempts = overview.RecentLoginAttempts,
            Roles = roles,
            CanAccessBackOffice = roles.Intersect(RoleNames.BackOffice, StringComparer.Ordinal).Any(),
            Membership = catalog.Membership,
            FeaturedApplications = featured,
            AccessibleApplicationCount = catalog.AccessibleCount,
            LockedApplicationCount = catalog.LockedCount,
            ComingSoonApplicationCount = catalog.ComingSoonCount,
        });
    }
}
