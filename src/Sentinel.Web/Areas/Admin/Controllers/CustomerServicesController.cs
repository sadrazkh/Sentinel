using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Application.Users;
using Sentinel.Domain.Identity;
using Sentinel.Vpn.Plans;
using Sentinel.Vpn.Provisioning;
using Sentinel.Web.Areas.Admin.Models;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Localization;

namespace Sentinel.Web.Areas.Admin.Controllers;

/// <summary>
/// Members' provisioned VPN services.
/// <para>
/// Reading is open to back-office read access so support can answer "is my service working"; every
/// action that changes a service, and therefore eventually touches a panel, needs write access.
/// </para>
/// <para>
/// Nothing here calls a panel itself. Each action records the intent and queues a job, which is what
/// makes the intent survive the process dying mid-call — precisely the moment nobody knows whether a
/// panel write took effect.
/// </para>
/// </summary>
[Area("Admin")]
[Authorize(Policy = PolicyNames.BackOfficeRead)]
[Route("Admin/CustomerServices")]
public sealed class CustomerServicesController : Controller
{
    /// <summary>
    /// How many members and plans the create form offers. A picker, not a directory: an operator
    /// creating a service for somebody already knows who, and rendering every account into a select
    /// would be a slow page and a needless dump of the member list into one view's HTML.
    /// </summary>
    private const int PickerSize = 200;

    private readonly ICustomerServiceQuery _query;
    private readonly ICustomerServiceManager _services;
    private readonly IUserAdminQuery _users;
    private readonly IServicePlanAdminQuery _plans;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public CustomerServicesController(
        ICustomerServiceQuery query,
        ICustomerServiceManager services,
        IUserAdminQuery users,
        IServicePlanAdminQuery plans,
        IStringLocalizer<SharedResource> localizer)
    {
        _query = query;
        _services = services;
        _users = users;
        _plans = plans;
        _localizer = localizer;
    }

    private bool CanWrite => User.IsInRole(RoleNames.SuperAdmin) || User.IsInRole(RoleNames.Admin);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(new CustomerServiceListViewModel
        {
            Services = await _query.ListAsync(cancellationToken),
            CanWrite = CanWrite,
            TimeZoneId = UserTime.DefaultTimeZoneId,
        });

    [HttpGet("new")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken) =>
        View(await PopulateAsync(new CustomerServiceCreateViewModel(), cancellationToken));

    [HttpPost("new")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Create(
        CustomerServiceCreateViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(await PopulateAsync(model, cancellationToken));
        }

        // Only the member, the plan and a note cross this boundary. The terms come from the plan and
        // the placement from the selector, so there is nothing else for the request to carry.
        var result = await _services.CreateAsync(
            new CreateServiceRequest(model.UserId, model.PlanId, model.Notes), cancellationToken);

        if (!result.Succeeded)
        {
            ModelState.AddModelError(
                string.Empty, _localizer[result.ErrorKey ?? ServiceErrors.NotFound].Value);

            return View(await PopulateAsync(model, cancellationToken));
        }

        TempData["StatusMessage"] = _localizer["admin.service.created"].Value;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/suspend")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public Task<IActionResult> Suspend(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(
            _services.SuspendAsync(id, cancellationToken), "admin.service.suspended");

    [HttpPost("{id:guid}/resume")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public Task<IActionResult> Resume(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(
            _services.ResumeAsync(id, cancellationToken), "admin.service.resumed");

    [HttpPost("{id:guid}/renew")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Renew(
        Guid id,
        CustomerServiceRenewViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = _localizer["admin.error.planDurationInvalid"].Value;
            return RedirectToAction(nameof(Index));
        }

        return await ApplyAsync(
            _services.RenewAsync(id, model.AdditionalDays, cancellationToken),
            "admin.service.renewed");
    }

    [HttpPost("{id:guid}/reset-traffic")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public Task<IActionResult> ResetTraffic(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(
            _services.ResetTrafficAsync(id, cancellationToken), "admin.service.trafficReset");

    /// <summary>
    /// Ends a service and removes its client from the panel.
    /// <para>
    /// The one irreversible action here, which is why the view puts a confirmation in front of it.
    /// The delivery link dies immediately rather than when the panel answers.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/decommission")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public Task<IActionResult> Decommission(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(
            _services.DecommissionAsync(id, cancellationToken), "admin.service.decommissioned");

    // -------------------------------------------------------------------------- helpers ----

    private async Task<IActionResult> ApplyAsync(Task<OperationResult> operation, string successKey)
    {
        var result = await operation;

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer[successKey].Value
            : _localizer[result.ErrorKey ?? ServiceErrors.NotFound].Value;

        return RedirectToAction(nameof(Index));
    }

    private async Task<CustomerServiceCreateViewModel> PopulateAsync(
        CustomerServiceCreateViewModel model,
        CancellationToken cancellationToken)
    {
        model.PickerSize = PickerSize;

        var members = await _users.SearchAsync(
            new UserListRequest(PageSize: PickerSize).Normalized(), cancellationToken);

        model.Members = members.Items
            .Select(user => (user.Id, Label: $"{user.DisplayName} ({user.UserName})"))
            .ToList();

        var plans = await _plans.ListAsync(cancellationToken);

        // Only what can actually be sold. Offering a hidden or unpurchasable plan here would let the
        // back office create services on terms the catalogue has deliberately withdrawn.
        model.Plans = plans
            .Where(plan => plan.IsVisible && plan.IsPurchasable)
            .Select(plan => (plan.Id, Label: $"{plan.ProductNameEn} — {plan.NameEn}"))
            .ToList();

        return model;
    }
}
