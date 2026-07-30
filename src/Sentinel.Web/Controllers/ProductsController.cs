using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Accounts;
using Sentinel.Application.Auditing;
using Sentinel.Application.Authorization;
using Sentinel.Application.Features;
using Sentinel.Application.Products;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Products;
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
    private readonly IProductContentService _content;
    private readonly IAccountOverviewQuery _accountOverview;
    private readonly IAuditService _audit;
    private readonly IFeatureGate _features;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(
        IProductLibraryService library,
        IProductContentService content,
        IAccountOverviewQuery accountOverview,
        IAuditService audit,
        IFeatureGate features,
        ILogger<ProductsController> logger)
    {
        _library = library;
        _content = content;
        _accountOverview = accountOverview;
        _audit = audit;
        _features = features;
        _logger = logger;
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
        var content = await _content.GetPageContentAsync(userId, key, cancellationToken);

        return View(new ProductDetailViewModel
        {
            Detail = detail,
            Content = content,
            TimeZoneId = overview?.TimeZoneId ?? UserTime.DefaultTimeZoneId,
            DocumentationEnabled = _features.IsEnabled(FeatureNames.ProductDocumentation),
        });
    }

    /// <summary>The documentation index for one product.</summary>
    [HttpGet("{key}/docs")]
    [RequireFeature(FeatureNames.ProductDocumentation)]
    public async Task<IActionResult> Docs(
        string key,
        [StringLength(80)] string? search,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var index = await _content.GetDocumentationIndexAsync(userId, key, cancellationToken);

        if (index is null)
        {
            return NotFound();
        }

        // An over-long term is dropped rather than rejected: a GET should still render.
        var term = ModelState.IsValid ? search : null;

        var matches = string.IsNullOrWhiteSpace(term)
            ? null
            : await _content.SearchArticlesAsync(userId, key, term, cancellationToken);

        return View(new DocumentationIndexViewModel
        {
            Index = index,
            Search = term,
            Matches = matches,
        });
    }

    [HttpGet("{key}/docs/{slug}")]
    [RequireFeature(FeatureNames.ProductDocumentation)]
    public async Task<IActionResult> Article(
        string key,
        string slug,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var article = await _content.GetArticleAsync(userId, key, slug, cancellationToken);

        // Absent, unpublished and out-of-audience answer identically — see the service.
        if (article is null)
        {
            return NotFound();
        }

        var overview = await _accountOverview.GetAsync(userId, 1, cancellationToken);

        return View(new DocumentationArticleViewModel
        {
            Article = article,
            TimeZoneId = overview?.TimeZoneId ?? UserTime.DefaultTimeZoneId,
        });
    }

    /// <summary>
    /// Starts a download.
    /// <para>
    /// Mirrors the application launch deliberately: the client never holds the destination, it
    /// asks the portal for a download by id, the decision is made here, the URL is re-validated,
    /// and only then is a redirect issued. That is what makes a locked download a real control.
    /// </para>
    /// </summary>
    [HttpGet("{key}/downloads/{id:guid}")]
    public async Task<IActionResult> Download(
        string key,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var resolution = await _content.ResolveDownloadAsync(userId, key, id, cancellationToken);

        if (resolution is null)
        {
            return NotFound();
        }

        if (!resolution.IsAllowed)
        {
            await _audit.RecordAndSaveAsync(
                AuditEntry.For(AuditActions.DownloadDenied, nameof(ProductDownload), resolution.DownloadId) with
                {
                    Result = AuditResult.Denied,
                    Metadata = AuditMetadata.Create().Set("productKey", resolution.ProductKey),
                },
                cancellationToken);

            return Forbid();
        }

        // Re-validated even though it passed the same policy when saved: this is the moment the
        // browser is told to follow the value, so a row that predates the rule — or arrived via a
        // database restore — must not be trusted here.
        if (!DownloadUrlPolicy.IsAllowed(resolution.Url))
        {
            _logger.LogError(
                "Download {DownloadId} for product {ProductKey} has a URL that fails the policy; refusing.",
                resolution.DownloadId,
                resolution.ProductKey);

            await _audit.RecordAndSaveAsync(
                AuditEntry.For(AuditActions.DownloadDenied, nameof(ProductDownload), resolution.DownloadId) with
                {
                    Result = AuditResult.Failure,
                    Metadata = AuditMetadata.Create()
                        .Set("productKey", resolution.ProductKey)
                        .Set("reason", "invalidDownloadUrl"),
                },
                cancellationToken);

            return Forbid();
        }

        await _audit.RecordAndSaveAsync(
            AuditEntry.For(AuditActions.DownloadStarted, nameof(ProductDownload), resolution.DownloadId) with
            {
                Metadata = AuditMetadata.Create().Set("productKey", resolution.ProductKey),
            },
            cancellationToken);

        // A deliberate off-site redirect, not an open redirect: the target comes from the
        // catalogue an operator curates, never from the request.
        return Redirect(resolution.Url!);
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
