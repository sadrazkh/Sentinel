using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Sentinel.Application.Authorization;
using Sentinel.Application.Catalog;
using Sentinel.Application.Common;
using Sentinel.Domain.Identity;
using Sentinel.Infrastructure.Media;
using Sentinel.Web.Areas.Admin.Models;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Localization;

namespace Sentinel.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Policy = PolicyNames.BackOfficeRead)]
public sealed class ApplicationsController : Controller
{
    private readonly IApplicationAdminQuery _query;
    private readonly IApplicationAdminService _applications;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly MediaStorageOptions _mediaOptions;

    public ApplicationsController(
        IApplicationAdminQuery query,
        IApplicationAdminService applications,
        IStringLocalizer<SharedResource> localizer,
        IOptions<MediaStorageOptions> mediaOptions)
    {
        _query = query;
        _applications = applications;
        _localizer = localizer;
        _mediaOptions = mediaOptions.Value;
    }

    private bool CanWrite => User.IsInRole(RoleNames.SuperAdmin) || User.IsInRole(RoleNames.Admin);

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var applications = await _query.ListAsync(cancellationToken);

        return View(new ApplicationListViewModel
        {
            Applications = applications,
            CanWrite = CanWrite,
            TimeZoneId = UserTime.DefaultTimeZoneId,
        });
    }

    [HttpGet]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public IActionResult Create()
    {
        ViewData["MaxIconBytes"] = _mediaOptions.MaxIconBytes;
        return View("Edit", new ApplicationEditViewModel());
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Create(
        ApplicationEditViewModel model,
        CancellationToken cancellationToken)
    {
        ViewData["MaxIconBytes"] = _mediaOptions.MaxIconBytes;

        if (!ModelState.IsValid)
        {
            return View("Edit", model);
        }

        var result = await _applications.CreateAsync(model.ToRequest(), cancellationToken);

        if (!result.Succeeded)
        {
            AddError(result);
            return View("Edit", model);
        }

        TempData["StatusMessage"] = _localizer["admin.application.created"].Value;
        return RedirectToAction(nameof(Edit), new { id = result.Value });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var application = await _query.GetForEditAsync(id, cancellationToken);

        if (application is null)
        {
            return NotFound();
        }

        ViewData["MaxIconBytes"] = _mediaOptions.MaxIconBytes;
        return View(ApplicationEditViewModel.From(application));
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Edit(
        Guid id,
        ApplicationEditViewModel model,
        CancellationToken cancellationToken)
    {
        ViewData["MaxIconBytes"] = _mediaOptions.MaxIconBytes;
        model.Id = id;

        if (!ModelState.IsValid)
        {
            model.IconPath = await CurrentIconAsync(id, cancellationToken);
            return View(model);
        }

        var result = await _applications.UpdateAsync(id, model.ToRequest(), cancellationToken);

        if (!result.Succeeded)
        {
            AddError(result);
            model.IconPath = await CurrentIconAsync(id, cancellationToken);
            return View(model);
        }

        TempData["StatusMessage"] = _localizer["admin.application.saved"].Value;
        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>
    /// Replaces the icon.
    /// <para>
    /// <c>RequestSizeLimit</c> makes Kestrel reject an oversized body before the framework
    /// buffers it, which is a different control from the byte-count check inside the service:
    /// this one stops the upload arriving at all, that one is what an honest-but-wrong
    /// <c>Content-Length</c> runs into.
    /// </para>
    /// </summary>
    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> UploadIcon(
        UploadIconViewModel model,
        CancellationToken cancellationToken)
    {
        if (model.Icon is null || model.Icon.Length == 0)
        {
            TempData["StatusMessage"] = _localizer[CatalogErrors.IconEmpty].Value;
            return RedirectToAction(nameof(Edit), new { id = model.ApplicationId });
        }

        await using var stream = model.Icon.OpenReadStream();

        var result = await _applications.ReplaceIconAsync(
            model.ApplicationId, stream, model.Icon.Length, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.application.iconSaved"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.IdentityRejected].Value;

        return RedirectToAction(nameof(Edit), new { id = model.ApplicationId });
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> RemoveIcon(Guid id, CancellationToken cancellationToken)
    {
        var result = await _applications.RemoveIconAsync(id, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.application.iconRemoved"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.IdentityRejected].Value;

        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task<string?> CurrentIconAsync(Guid id, CancellationToken cancellationToken) =>
        (await _query.GetForEditAsync(id, cancellationToken))?.IconPath;

    private void AddError(OperationResult result) =>
        ModelState.AddModelError(
            string.Empty,
            _localizer[result.ErrorKey ?? OperationErrors.IdentityRejected].Value);
}
