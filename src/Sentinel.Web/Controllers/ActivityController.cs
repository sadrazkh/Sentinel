using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Accounts;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Models.Account;

namespace Sentinel.Web.Controllers;

[Authorize(Policy = PolicyNames.ActiveUser)]
public sealed class ActivityController : Controller
{
    private readonly IActivityQuery _activity;
    private readonly IProfileService _profile;

    public ActivityController(IActivityQuery activity, IProfileService profile)
    {
        _activity = activity;
        _profile = profile;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        int page = 1,
        int pageSize = PagingDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        // The history is scoped by the authenticated principal, never by a route value, so
        // there is nothing to change in the URL to see somebody else's sign-ins.
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Forbid();
        }

        var history = await _activity.GetSignInHistoryAsync(userId, page, pageSize, cancellationToken);
        var profile = await _profile.GetAsync(userId, cancellationToken);

        return View(new ActivityViewModel
        {
            History = history,
            TimeZoneId = profile?.TimeZoneId ?? UserTime.DefaultTimeZoneId,
        });
    }
}
