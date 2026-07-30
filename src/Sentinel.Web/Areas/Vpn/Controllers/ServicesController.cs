using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Accounts;
using Sentinel.Application.Authorization;
using Sentinel.Application.Features;
using Sentinel.Application.Products;
using Sentinel.Application.Subscriptions;
using Sentinel.Vpn.Plans;
using Sentinel.Vpn.Provisioning;
using Sentinel.Vpn.Purchasing;
using Sentinel.Web.Areas.Vpn.Models;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Localization;
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
    private readonly ICustomerServiceQuery _services;
    private readonly ICustomerServiceManager _manager;
    private readonly IPlanPurchaseService _purchases;
    private readonly ISubscriptionService _subscriptions;
    private readonly IAccountOverviewQuery _accountOverview;
    private readonly IFeatureGate _features;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ServicesController(
        IProductLibraryService library,
        IProductContentService content,
        IServicePlanCatalog plans,
        ICustomerServiceQuery services,
        ICustomerServiceManager manager,
        IPlanPurchaseService purchases,
        ISubscriptionService subscriptions,
        IAccountOverviewQuery accountOverview,
        IFeatureGate features,
        IStringLocalizer<SharedResource> localizer)
    {
        _library = library;
        _content = content;
        _plans = plans;
        _services = services;
        _manager = manager;
        _purchases = purchases;
        _subscriptions = subscriptions;
        _accountOverview = accountOverview;
        _features = features;
        _localizer = localizer;
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

        // Services this portal provisions, scoped to this product and this member by the query
        // itself. Always read: unlike the external links below, these are the portal's own records
        // and cost one indexed query, not a round trip to somebody else's server.
        var managed = await _services.GetForUserAndProductAsync(
            userId, detail.Card.Id, cancellationToken);

        // The member's external subscription links. Read without forcing a refresh: opening a page
        // must not make the server reach out to every upstream the member has registered.
        var services = _features.IsEnabled(FeatureNames.ExternalSubscriptions)
            ? await _subscriptions.GetForUserAsync(userId, forceRefresh: false, cancellationToken)
            : [];

        var availability = new VpnTabAvailability(
            plans.HasPlans,
            managed.Count + services.Count,

            // A managed service's configurations live on the panel and are fetched through the
            // delivery URL, not held here — so it contributes its link, not a config count.
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
            ManagedServices = managed,
            Services = services,

            // Built from the live request rather than configuration: behind a reverse proxy the
            // forwarded-headers middleware has already corrected these, and hard-coding a host is
            // how a staging deployment ends up handing out production links.
            DeliveryBaseUrl = $"{Request.Scheme}://{Request.Host}",
            Content = content,
            TimeZoneId = overview?.TimeZoneId ?? UserTime.DefaultTimeZoneId,
            SelfServiceEnabled = _features.IsEnabled(FeatureNames.VpnSelfService),
        });
    }

    /// <summary>
    /// Issues a fresh delivery URL for one of the member's own services.
    /// <para>
    /// The only remedy once a subscription URL has leaked, so it is offered to the member rather than
    /// kept behind a support request. The service id comes from the form, but ownership is re-checked
    /// in the manager against the signed-in user — the form value decides nothing on its own.
    /// </para>
    /// </summary>
    [HttpPost("link/rotate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateLink(
        string key,
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var result = await _manager.RotateDeliveryTokenAsync(serviceId, userId, cancellationToken);

        // The new token is deliberately not carried in the redirect. A query string or fragment
        // lands in browser history, in a proxy log and in the next request's referrer — and this
        // value is the credential. The redirected page re-reads it from the sealed copy instead.
        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["vpn.service.link.rotated"].Value
            : _localizer[result.ErrorKey ?? ServiceErrors.NotFound].Value;

        return RedirectToAction(nameof(Index), new { key, tab = "services" });
    }

    /// <summary>
    /// Buys a plan with wallet credit.
    /// <para>
    /// The form sends a plan and an idempotency key. It does <b>not</b> send a price — the amount
    /// charged is read from the plan row inside the transaction, so a crafted post cannot name its
    /// own. Whether the plan is on sale and whether this member is in its audience are re-decided
    /// there too: the catalogue that drew the button is a rendering, not an authorisation.
    /// </para>
    /// <para>
    /// Gated on both flags, and gated again inside the service. Off in production until the purchase
    /// flow has had its own security review.
    /// </para>
    /// </summary>
    [HttpPost("purchase")]
    [ValidateAntiForgeryToken]
    [RequireFeature(FeatureNames.Purchases)]
    [RequireFeature(FeatureNames.Wallet)]
    public async Task<IActionResult> Purchase(
        string key,
        Guid planId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var result = await _purchases.PurchaseAsync(
            userId, new PurchasePlanRequest(planId, idempotencyKey), cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["purchase.done"].Value
            : _localizer[result.ErrorKey ?? PurchaseErrors.PlanNotFound].Value;

        return RedirectToAction(
            nameof(Index), new { key, tab = result.Succeeded ? "services" : null });
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
