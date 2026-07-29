using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Accounts;
using Sentinel.Application.Authorization;
using Sentinel.Application.Features;
using Sentinel.Application.Products;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Models.Products;
using Sentinel.Web.Security;

namespace Sentinel.Web.Controllers;

/// <summary>
/// The product library: what a member can discover, what they hold, and the details of one item.
/// <para>
/// Every action re-derives access through <see cref="IProductLibraryService"/>. Nothing here
/// filters by anything the request supplied beyond a category and a search term, both of which
/// are applied to an already-authorised set — so a crafted query can narrow what a member sees
/// but never widen it.
/// </para>
/// </summary>
[Authorize(Policy = PolicyNames.ActiveUser)]
[Route("products")]
[RequireFeature(FeatureNames.ProductLibrary)]
public sealed class ProductsController : Controller
{
    private readonly IProductLibraryService _library;
    private readonly IAccountOverviewQuery _accountOverview;
    private readonly IFeatureGate _features;

    public ProductsController(
        IProductLibraryService library,
        IAccountOverviewQuery accountOverview,
        IFeatureGate features)
    {
        _library = library;
        _accountOverview = accountOverview;
        _features = features;
    }

    /// <summary>Discover: everything visible to this member, held or not.</summary>
    [HttpGet("")]
    [RequireFeature(FeatureNames.ProductDiscovery)]
    public Task<IActionResult> Index(
        ProductLibraryInput input,
        CancellationToken cancellationToken) =>
        RenderLibraryAsync(input, ProductLibraryScope.Discover, cancellationToken);

    /// <summary>My library: only what this member has a relationship with.</summary>
    [HttpGet("library")]
    public Task<IActionResult> Library(
        ProductLibraryInput input,
        CancellationToken cancellationToken) =>
        RenderLibraryAsync(input, ProductLibraryScope.Mine, cancellationToken);

    [HttpGet("{key}")]
    public async Task<IActionResult> Details(string key, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var detail = await _library.GetDetailAsync(userId, key, cancellationToken);

        // Null covers both "no such product" and "not visible to you". Answering the same way
        // for each is deliberate: otherwise the details URL enumerates unreleased products.
        if (detail is null)
        {
            return NotFound();
        }

        var overview = await _accountOverview.GetAsync(userId, 1, cancellationToken);

        return View(new ProductDetailViewModel
        {
            Detail = detail,
            TimeZoneId = overview?.TimeZoneId ?? UserTime.DefaultTimeZoneId,
            DocumentationEnabled = _features.IsEnabled(FeatureNames.ProductDocumentation),
        });
    }

    private async Task<IActionResult> RenderLibraryAsync(
        ProductLibraryInput input,
        ProductLibraryScope scope,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        // An over-long search term is dropped rather than rejected: the page still works, the
        // member simply sees an unfiltered list instead of a validation error on a GET.
        if (!ModelState.IsValid)
        {
            input = new ProductLibraryInput();
        }

        var library = await _library.GetLibraryAsync(userId, input.ToQuery(scope), cancellationToken);
        var overview = await _accountOverview.GetAsync(userId, 1, cancellationToken);

        return View("Library", new ProductLibraryViewModel
        {
            Library = library,
            Scope = scope,
            TimeZoneId = overview?.TimeZoneId ?? UserTime.DefaultTimeZoneId,
            DiscoveryEnabled = _features.IsEnabled(FeatureNames.ProductDiscovery),
        });
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
