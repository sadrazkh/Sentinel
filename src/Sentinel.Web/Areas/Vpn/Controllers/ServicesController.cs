using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Accounts;
using Sentinel.Application.Authorization;
using Sentinel.Application.Features;
using Sentinel.Application.Products;
using Sentinel.Application.Subscriptions;
using Sentinel.Vpn.Plans;
using Sentinel.Web.Areas.Vpn.Models;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Security;

namespace Sentinel.Web.Areas.Vpn.Controllers;

/// <summary>
/// The VPN product page.
/// <para>
/// In its own area because VPN policy differs from every other product's — but it deliberately owns
/// no access logic. Whether the member may see the product is still
/// <see cref="IProductLibraryService"/>'s answer, and the page is a composition of the product
/// library, the subscription feature and the plan catalogue.
/// </para>
/// </summary>
[Area("Vpn")]
[Authorize(Policy = PolicyNames.ActiveUser)]
[Route("vpn/{key}")]
[RequireFeature(FeatureNames.ProductLibrary)]
public sealed class ServicesController : Controller
{
    private readonly IProductLibraryService _library;
    private readonly IProductContentService _content;
    private readonly IServicePlanCatalog _plans;
    private readonly ISubscriptionService _subscriptions;
    private readonly IAccountOverviewQuery _accountOverview;
    private readonly IFeatureGate _features;

    public ServicesController(
        IProductLibraryService library,
        IProductContentService content,
        IServicePlanCatalog plans,
        ISubscriptionService subscriptions,
        IAccountOverviewQuery accountOverview,
        IFeatureGate features)
    {
        _library = library;
        _content = content;
        _plans = plans;
        _subscriptions = subscriptions;
        _accountOverview = accountOverview;
        _features = features;
    }

    /// <summary>
    /// One tab of the product page.
    /// <para>
    /// The tab is part of the path rather than a query string, so each is a real, linkable page and
    /// an unrecognised name does not silently fall back to the first one.
    /// </para>
    /// </summary>
    [HttpGet("")]
    [HttpGet("{tab}")]
    public async Task<IActionResult> Index(
        string key,
        string? tab,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        // Absent and unparseable are different: no tab means the default, a bad one is a wrong URL.
        VpnProductTab requested;

        if (string.IsNullOrEmpty(tab))
        {
            requested = VpnProductTab.Overview;
        }
        else if (!Enum.TryParse(tab, ignoreCase: true, out requested))
        {
            return NotFound();
        }

        var detail = await _library.GetDetailAsync(userId, key, cancellationToken);

        // Null covers both "no such product" and "not visible to you", and answering the same way
        // for each is what stops this URL enumerating unreleased products.
        if (detail is null)
        {
            return NotFound();
        }

        var content = await _content.GetPageContentAsync(userId, key, cancellationToken);
        var plans = await _plans.GetForMemberAsync(userId, detail.Card.Id, cancellationToken);

        // The member's external subscription links. Read without forcing a refresh: opening a page
        // must not make the server reach out to every upstream the member has registered.
        var services = _features.IsEnabled(FeatureNames.ExternalSubscriptions)
            ? await _subscriptions.GetForUserAsync(userId, forceRefresh: false, cancellationToken)
            : [];

        var availability = new VpnTabAvailability(
            plans.HasPlans,
            services.Count,
            services.Sum(service => service.Configs.Count),
            content.Downloads.Count,
            content.TotalArticleCount);

        // A tab with nothing behind it is not a page. Redirecting rather than 404ing, because the
        // link was legitimate — it simply has no content for this member right now.
        if (!availability.IsAvailable(requested))
        {
            return requested == VpnProductTab.Overview
                ? NotFound()
                : RedirectToAction(nameof(Index), new { key, tab = (string?)null });
        }

        var overview = await _accountOverview.GetAsync(userId, 1, cancellationToken);

        return View(new VpnProductViewModel
        {
            Detail = detail,
            ActiveTab = requested,
            Tabs = availability,
            Plans = plans,
            Services = services,
            Content = content,
            TimeZoneId = overview?.TimeZoneId ?? UserTime.DefaultTimeZoneId,
            SelfServiceEnabled = _features.IsEnabled(FeatureNames.VpnSelfService),
        });
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
