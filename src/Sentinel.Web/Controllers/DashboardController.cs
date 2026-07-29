using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Accounts;
using Sentinel.Application.Authorization;
using Sentinel.Domain.Identity;
using Sentinel.Web.Models.Dashboard;

namespace Sentinel.Web.Controllers;

[Authorize(Policy = PolicyNames.ActiveUser)]
public sealed class DashboardController : Controller
{
    private const int RecentLoginCount = 5;

    private readonly IAccountOverviewQuery _accountOverview;

    public DashboardController(IAccountOverviewQuery accountOverview) => _accountOverview = accountOverview;

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

        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

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
        });
    }
}
