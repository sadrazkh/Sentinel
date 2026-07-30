using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Sentinel.Application.Authorization;
using Sentinel.Application.Catalog;
using Sentinel.Application.Common;
using Sentinel.Application.Media;
using Sentinel.Application.Products;
using Sentinel.Domain.Identity;
using Sentinel.Infrastructure.Media;
using Sentinel.Web.Areas.Admin.Models;
using Sentinel.Web.Localization;

namespace Sentinel.Web.Areas.Admin.Controllers;

/// <summary>
/// Authoring for one product's page sections, downloads and documentation.
/// <para>
/// Every write action requires <see cref="PolicyNames.BackOfficeWrite"/>; reads only require
/// read access, so support staff can look at what a member would see without being able to
/// change it.
/// </para>
/// </summary>
[Area("Admin")]
[Authorize(Policy = PolicyNames.BackOfficeRead)]
[Route("Admin/Content")]
public sealed class ContentController : Controller
{
    private readonly IProductContentAdminQuery _query;
    private readonly IProductContentAdminService _content;
    private readonly IApplicationAdminQuery _products;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly MediaStorageOptions _mediaOptions;

    public ContentController(
        IProductContentAdminQuery query,
        IProductContentAdminService content,
        IApplicationAdminQuery products,
        IStringLocalizer<SharedResource> localizer,
        IOptions<MediaStorageOptions> mediaOptions)
    {
        _query = query;
        _content = content;
        _products = products;
        _localizer = localizer;
        _mediaOptions = mediaOptions.Value;
    }

    private bool CanWrite => User.IsInRole(RoleNames.SuperAdmin) || User.IsInRole(RoleNames.Admin);

    // ------------------------------------------------------------------------ overview ----

    [HttpGet("{productId:guid}")]
    public async Task<IActionResult> Index(Guid productId, CancellationToken cancellationToken)
    {
        var product = await _products.GetForEditAsync(productId, cancellationToken);

        if (product is null)
        {
            return NotFound();
        }

        var summary = await _query.GetSummaryAsync(productId, cancellationToken);

        if (summary is null)
        {
            return NotFound();
        }

        return View(new ContentOverviewViewModel
        {
            ProductId = productId,
            ProductKey = product.Key,
            ProductNameFa = product.NameFa,
            ProductNameEn = product.NameEn,
            Summary = summary,
            Sections = await _query.ListSectionsAsync(productId, cancellationToken),
            Downloads = await _query.ListDownloadsAsync(productId, cancellationToken),
            Categories = await _query.ListCategoriesAsync(productId, cancellationToken),
            Articles = await _query.ListArticlesAsync(productId, cancellationToken),
            CanWrite = CanWrite,
        });
    }

    // ------------------------------------------------------------------------ sections ----

    [HttpGet("{productId:guid}/sections/new")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public IActionResult NewSection(Guid productId) =>
        View("EditSection", new SectionEditViewModel { ProductId = productId });

    [HttpGet("sections/{id:guid}")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> EditSection(Guid id, CancellationToken cancellationToken)
    {
        var section = await _query.GetSectionAsync(id, cancellationToken);

        return section is null ? NotFound() : View(SectionEditViewModel.From(section));
    }

    [HttpPost("{productId:guid}/sections")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> SaveSection(
        Guid productId,
        SectionEditViewModel model,
        CancellationToken cancellationToken)
    {
        model.ProductId = productId;

        if (!ModelState.IsValid)
        {
            return View("EditSection", model);
        }

        var result = await _content.SaveSectionAsync(
            productId,
            model.IsNew ? null : model.Id,
            model.ToRequest(),
            cancellationToken);

        if (!result.Succeeded)
        {
            AddError(result);
            return View("EditSection", model);
        }

        TempData["StatusMessage"] = _localizer["admin.content.saved"].Value;
        return RedirectToAction(nameof(Index), new { productId });
    }

    [HttpPost("{productId:guid}/sections/{id:guid}/delete")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> DeleteSection(
        Guid productId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _content.DeleteSectionAsync(id, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.content.deleted"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Index), new { productId });
    }

    // ----------------------------------------------------------------------- downloads ----

    [HttpGet("{productId:guid}/downloads/new")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public IActionResult NewDownload(Guid productId) =>
        View("EditDownload", new DownloadEditViewModel { ProductId = productId });

    [HttpGet("downloads/{id:guid}")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> EditDownload(Guid id, CancellationToken cancellationToken)
    {
        var download = await _query.GetDownloadAsync(id, cancellationToken);

        return download is null ? NotFound() : View(DownloadEditViewModel.From(download));
    }

    [HttpPost("{productId:guid}/downloads")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> SaveDownload(
        Guid productId,
        DownloadEditViewModel model,
        CancellationToken cancellationToken)
    {
        model.ProductId = productId;

        if (!ModelState.IsValid)
        {
            return View("EditDownload", model);
        }

        var result = await _content.SaveDownloadAsync(
            productId,
            model.IsNew ? null : model.Id,
            model.ToRequest(),
            cancellationToken);

        if (!result.Succeeded)
        {
            AddError(result);
            return View("EditDownload", model);
        }

        TempData["StatusMessage"] = _localizer["admin.content.saved"].Value;
        return RedirectToAction(nameof(Index), new { productId });
    }

    [HttpPost("{productId:guid}/downloads/{id:guid}/delete")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> DeleteDownload(
        Guid productId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _content.DeleteDownloadAsync(id, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.content.deleted"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Index), new { productId });
    }

    // ---------------------------------------------------------------------- categories ----

    [HttpGet("{productId:guid}/categories/new")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public IActionResult NewCategory(Guid productId) =>
        View("EditCategory", new CategoryEditViewModel { ProductId = productId });

    [HttpPost("{productId:guid}/categories")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> SaveCategory(
        Guid productId,
        CategoryEditViewModel model,
        CancellationToken cancellationToken)
    {
        model.ProductId = productId;

        if (!ModelState.IsValid)
        {
            return View("EditCategory", model);
        }

        var result = await _content.SaveCategoryAsync(
            productId,
            model.IsNew ? null : model.Id,
            model.ToRequest(),
            cancellationToken);

        if (!result.Succeeded)
        {
            AddError(result);
            return View("EditCategory", model);
        }

        TempData["StatusMessage"] = _localizer["admin.content.saved"].Value;
        return RedirectToAction(nameof(Index), new { productId });
    }

    [HttpPost("{productId:guid}/categories/{id:guid}/delete")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> DeleteCategory(
        Guid productId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _content.DeleteCategoryAsync(id, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.content.deleted"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Index), new { productId });
    }

    // ------------------------------------------------------------------------ articles ----

    [HttpGet("{productId:guid}/articles/new")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> NewArticle(Guid productId, CancellationToken cancellationToken)
    {
        await PrepareArticleFormAsync(productId, cancellationToken);

        // One blank step offered, so the shape of the form is obvious without a click.
        return View("EditArticle", new ArticleEditViewModel
        {
            ProductId = productId,
            Steps = [new StepFieldSet()],
        });
    }

    [HttpGet("articles/{id:guid}")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> EditArticle(Guid id, CancellationToken cancellationToken)
    {
        var article = await _query.GetArticleAsync(id, cancellationToken);

        if (article is null)
        {
            return NotFound();
        }

        await PrepareArticleFormAsync(article.ProductId, cancellationToken);

        var model = ArticleEditViewModel.From(article);
        model.Steps = await LoadStepFieldsAsync(id, cancellationToken);

        return View(model);
    }

    [HttpPost("{productId:guid}/articles")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> SaveArticle(
        Guid productId,
        ArticleEditViewModel model,
        CancellationToken cancellationToken)
    {
        model.ProductId = productId;

        await PrepareArticleFormAsync(productId, cancellationToken);

        if (!ModelState.IsValid)
        {
            return View("EditArticle", model);
        }

        var result = await _content.SaveArticleAsync(
            productId,
            model.IsNew ? null : model.Id,
            model.ToRequest(),
            cancellationToken);

        if (!result.Succeeded)
        {
            AddError(result);
            return View("EditArticle", model);
        }

        var articleId = result.Value;

        var steps = await _content.SaveStepsAsync(articleId, model.ToStepInputs(), cancellationToken);

        if (!steps.Succeeded)
        {
            AddError(steps);
            model.Id = articleId;
            return View("EditArticle", model);
        }

        TempData["StatusMessage"] = _localizer["admin.content.saved"].Value;

        // Back to the article rather than the overview: attaching a step image needs the steps to
        // exist first, so the operator is left where they can do it.
        return RedirectToAction(nameof(EditArticle), new { id = articleId });
    }

    [HttpPost("{productId:guid}/articles/{id:guid}/delete")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> DeleteArticle(
        Guid productId,
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _content.DeleteArticleAsync(id, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.content.deleted"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Index), new { productId });
    }

    /// <summary>
    /// Attaches a screenshot to one step.
    /// <para>
    /// The bytes decide the format, never the file name or the browser's Content-Type — the same
    /// rule the product icon upload follows, for the same reason.
    /// </para>
    /// </summary>
    [HttpPost("articles/{id:guid}/steps/{step:int}/image")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> UploadStepImage(
        Guid id,
        int step,
        StepImageUploadViewModel model,
        CancellationToken cancellationToken)
    {
        if (model.Image is null || model.Image.Length == 0)
        {
            TempData["StatusMessage"] = _localizer[CatalogErrors.IconEmpty].Value;
            return RedirectToAction(nameof(EditArticle), new { id });
        }

        await using var stream = model.Image.OpenReadStream();

        // The service buffers, checks the byte signature and decides the format. The controller
        // deliberately makes no judgement about the bytes.
        var result = await _content.SetStepImageAsync(
            id, step, stream, model.Image.Length, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.content.saved"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(EditArticle), new { id });
    }

    [HttpPost("articles/{id:guid}/steps/{step:int}/image/delete")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> ClearStepImage(
        Guid id,
        int step,
        CancellationToken cancellationToken)
    {
        var result = await _content.ClearStepImageAsync(id, step, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.content.deleted"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(EditArticle), new { id });
    }

    // ------------------------------------------------------------------------- helpers ----

    private async Task PrepareArticleFormAsync(Guid productId, CancellationToken cancellationToken)
    {
        ViewData["Categories"] = await _query.ListCategoriesAsync(productId, cancellationToken);
        ViewData["MaxIconBytes"] = _mediaOptions.MaxIconBytes;
    }

    /// <summary>
    /// Loads the existing steps as form rows, plus one blank row so an operator can always add
    /// another without needing scripting.
    /// </summary>
    private async Task<List<StepFieldSet>> LoadStepFieldsAsync(
        Guid articleId,
        CancellationToken cancellationToken)
    {
        var steps = await _query.ListStepsAsync(articleId, cancellationToken);

        var fields = steps
            .Select(step => new StepFieldSet
            {
                TitleFa = step.TitleFa,
                TitleEn = step.TitleEn,
                BodyFa = step.BodyFa,
                BodyEn = step.BodyEn,
                MediaPath = step.MediaPath,
            })
            .ToList();

        // One spare row, so an operator can always add a step without needing scripting.
        fields.Add(new StepFieldSet());

        return fields;
    }

    private void AddError(OperationResult result) =>
        ModelState.AddModelError(
            string.Empty,
            _localizer[result.ErrorKey ?? OperationErrors.IdentityRejected].Value);
}
