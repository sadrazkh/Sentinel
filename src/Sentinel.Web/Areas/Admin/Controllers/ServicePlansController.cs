using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Authorization;
using Sentinel.Application.Catalog;
using Sentinel.Application.Common;
using Sentinel.Domain.Identity;
using Sentinel.Vpn.Plans;
using Sentinel.Web.Areas.Admin.Models;
using Sentinel.Web.Localization;

namespace Sentinel.Web.Areas.Admin.Controllers;

/// <summary>
/// Service plans and who they are offered to.
/// <para>
/// Everything a customer would be charged and everything they would receive is written here, by an
/// operator with write access. There is no member-facing path to any of these values.
/// </para>
/// </summary>
[Area("Admin")]
[Authorize(Policy = PolicyNames.BackOfficeRead)]
[Route("Admin/ServicePlans")]
public sealed class ServicePlansController : Controller
{
    private readonly IServicePlanAdminQuery _query;
    private readonly IServicePlanAdminService _plans;
    private readonly IApplicationAdminQuery _products;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ServicePlansController(
        IServicePlanAdminQuery query,
        IServicePlanAdminService plans,
        IApplicationAdminQuery products,
        IStringLocalizer<SharedResource> localizer)
    {
        _query = query;
        _plans = plans;
        _products = products;
        _localizer = localizer;
    }

    private bool CanWrite => User.IsInRole(RoleNames.SuperAdmin) || User.IsInRole(RoleNames.Admin);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(new ServicePlanListViewModel
        {
            Plans = await _query.ListAsync(cancellationToken),
            CanWrite = CanWrite,
        });

    [HttpGet("new")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        await PrepareFormAsync(cancellationToken);

        return View("Edit", new ServicePlanEditViewModel());
    }

    [HttpPost("new")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Create(
        ServicePlanEditViewModel model,
        CancellationToken cancellationToken)
    {
        await PrepareFormAsync(cancellationToken);

        if (!ModelState.IsValid)
        {
            return View("Edit", model);
        }

        var result = await _plans.SaveAsync(null, model.ToRequest(), cancellationToken);

        if (!result.Succeeded)
        {
            AddError(result);
            return View("Edit", model);
        }

        TempData["StatusMessage"] = _localizer["admin.plan.saved"].Value;

        // Straight to the plan, where the audience rules are: a plan with no rules is open to
        // everyone, which is often not what the operator meant.
        return RedirectToAction(nameof(Edit), new { id = result.Value });
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var plan = await _query.GetForEditAsync(id, cancellationToken);

        if (plan is null)
        {
            return NotFound();
        }

        await PrepareFormAsync(cancellationToken);

        return View(ServicePlanEditViewModel.From(plan));
    }

    [HttpPost("{id:guid}")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Edit(
        Guid id,
        ServicePlanEditViewModel model,
        CancellationToken cancellationToken)
    {
        model.Id = id;

        await PrepareFormAsync(cancellationToken);

        if (!ModelState.IsValid)
        {
            model.AudienceRules = (await _query.GetForEditAsync(id, cancellationToken))?.AudienceRules ?? [];
            return View(model);
        }

        var result = await _plans.SaveAsync(id, model.ToRequest(), cancellationToken);

        if (!result.Succeeded)
        {
            AddError(result);
            model.AudienceRules = (await _query.GetForEditAsync(id, cancellationToken))?.AudienceRules ?? [];
            return View(model);
        }

        TempData["StatusMessage"] = _localizer["admin.plan.saved"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/delete")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _plans.DeleteAsync(id, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.plan.deleted"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Index));
    }

    // ---------------------------------------------------------------------- audience ----

    [HttpPost("{id:guid}/rules")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> AddRule(
        Guid id,
        AudienceRuleInputModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = _localizer[PlanErrors.RuleIncomplete].Value;
            return RedirectToAction(nameof(Edit), new { id });
        }

        var result = await _plans.AddRuleAsync(id, model.ToRequest(), cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.plan.audience.ruleAdded"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("{id:guid}/rules/{ruleId:guid}/delete")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> RemoveRule(
        Guid id,
        Guid ruleId,
        CancellationToken cancellationToken)
    {
        var result = await _plans.RemoveRuleAsync(ruleId, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.plan.audience.ruleRemoved"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>
    /// The products a plan can belong to. Loaded on every render of the form, including the
    /// failure paths, so a validation error never comes back with an empty product list.
    /// </summary>
    private async Task PrepareFormAsync(CancellationToken cancellationToken) =>
        ViewData["Products"] = await _products.ListAsync(cancellationToken);

    private void AddError(OperationResult result) =>
        ModelState.AddModelError(
            string.Empty,
            _localizer[result.ErrorKey ?? OperationErrors.IdentityRejected].Value);
}
