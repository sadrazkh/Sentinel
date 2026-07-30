using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Application.Users;
using Sentinel.Domain.Identity;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Migration;
using Sentinel.Vpn.Plans;
using Sentinel.Vpn.Provisioning;
using Sentinel.Vpn.Servers;
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
    private readonly IServiceMigrationManager _migrations;
    private readonly IVpnServerAdminQuery _servers;
    private readonly IUserAdminQuery _users;
    private readonly IServicePlanAdminQuery _plans;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public CustomerServicesController(
        ICustomerServiceQuery query,
        ICustomerServiceManager services,
        IServiceMigrationManager migrations,
        IVpnServerAdminQuery servers,
        IUserAdminQuery users,
        IServicePlanAdminQuery plans,
        IStringLocalizer<SharedResource> localizer)
    {
        _query = query;
        _services = services;
        _migrations = migrations;
        _servers = servers;
        _users = users;
        _plans = plans;
        _localizer = localizer;
    }

    private bool CanWrite => User.IsInRole(RoleNames.SuperAdmin) || User.IsInRole(RoleNames.Admin);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var migrations = await _migrations.ListAsync(cancellationToken);

        return View(new CustomerServiceListViewModel
        {
            Services = await _query.ListAsync(cancellationToken),

            // Only the unfinished ones, keyed by service, so a row can say "moving" instead of
            // offering actions that would be refused.
            InFlightMigrations = migrations
                .Where(migration => !migration.IsFinished)
                .GroupBy(migration => migration.ServiceId)
                .ToDictionary(group => group.Key, group => group.First()),

            CanWrite = CanWrite,
            TimeZoneId = UserTime.DefaultTimeZoneId,
        });
    }

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
    public Task<IActionResult> Suspend([FromRoute] Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(
            _services.SuspendAsync(id, cancellationToken), "admin.service.suspended");

    [HttpPost("{id:guid}/resume")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public Task<IActionResult> Resume([FromRoute] Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(
            _services.ResumeAsync(id, cancellationToken), "admin.service.resumed");

    [HttpPost("{id:guid}/renew")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Renew(
        [FromRoute] Guid id,
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
    public Task<IActionResult> ResetTraffic([FromRoute] Guid id, CancellationToken cancellationToken) =>
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
    public Task<IActionResult> Decommission([FromRoute] Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(
            _services.DecommissionAsync(id, cancellationToken), "admin.service.decommissioned");

    // ------------------------------------------------------------------------ migration ----

    /// <summary>
    /// Every migration, in progress and historical.
    /// <para>
    /// Its own page rather than a column on the list: the thing an operator needs to watch is the
    /// dual-active window, which is a duration rather than a status, and it belongs where it can be
    /// read at a glance.
    /// </para>
    /// </summary>
    [HttpGet("migrations")]
    public async Task<IActionResult> Migrations(CancellationToken cancellationToken) =>
        View(new MigrationListViewModel
        {
            Migrations = await _migrations.ListAsync(cancellationToken),
            CanWrite = CanWrite,
            TimeZoneId = UserTime.DefaultTimeZoneId,
        });

    [HttpGet("{id:guid}/migrate")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Migrate([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var model = await PopulateAsync(
            new ServiceMigrationCreateViewModel { ServiceId = id }, cancellationToken);

        return model is null ? NotFound() : View(model);
    }

    [HttpPost("{id:guid}/migrate")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Migrate(
        [FromRoute] Guid id,
        ServiceMigrationCreateViewModel model,
        CancellationToken cancellationToken)
    {
        model.ServiceId = id;

        if (!ModelState.IsValid)
        {
            return await RedisplayAsync(model, cancellationToken);
        }

        // The route's id, not the form's — and the destination is the only other thing that crosses
        // this boundary. The allowance and the expiry are computed from the source panel and the
        // service row, so there is nothing here for a crafted post to set.
        var result = await _migrations.PlanAsync(
            new MigrateServiceRequest(
                id,
                model.DestinationServerId == Guid.Empty ? null : model.DestinationServerId,
                model.CountryCode,
                model.Reason),
            cancellationToken);

        if (!result.Succeeded)
        {
            ModelState.AddModelError(
                string.Empty, _localizer[result.ErrorKey ?? MigrationErrors.ServiceNotFound].Value);

            return await RedisplayAsync(model, cancellationToken);
        }

        TempData["StatusMessage"] = _localizer["admin.migration.planned"].Value;

        return RedirectToAction(nameof(Migrations));
    }

    [HttpPost("migrations/{migrationId:guid}/rollback")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> RollBackMigration(
        [FromRoute] Guid migrationId,
        CancellationToken cancellationToken)
    {
        var result = await _migrations.RollBackAsync(migrationId, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.migration.rolledBack"].Value
            : _localizer[result.ErrorKey ?? MigrationErrors.ServiceNotFound].Value;

        return RedirectToAction(nameof(Migrations));
    }

    // -------------------------------------------------------------------------- helpers ----

    /// <summary>
    /// Fills in everything the form displays but does not bind.
    /// <para>
    /// Re-run on the failed-post path as well as on the initial GET, and it has to be: the member
    /// name, the plan and — the one that matters — which server the service is currently on are not
    /// posted back. Without reloading them, a rejected submission would redisplay a page with the
    /// context blank and the service's own server offered as a destination.
    /// </para>
    /// <para>
    /// Returns <c>null</c> when the service does not exist, which the caller turns into a 404.
    /// </para>
    /// </summary>
    private async Task<ServiceMigrationCreateViewModel?> PopulateAsync(
        ServiceMigrationCreateViewModel model,
        CancellationToken cancellationToken)
    {
        var service = (await _query.ListAsync(cancellationToken))
            .FirstOrDefault(candidate => candidate.Id == model.ServiceId);

        if (service is null)
        {
            return null;
        }

        model.UserName = service.UserName;
        model.PlanNameEn = service.PlanNameEn;
        model.CurrentServerKey = service.ServerKey;

        var servers = await _servers.ListAsync(cancellationToken);

        // Only servers a service could actually land on, and never the one it is already on. Offering
        // an unusable destination would be an operator choosing a placement the planner then refuses.
        model.Destinations = servers
            .Where(server => server.Status == VpnServerStatus.Active
                             && server.Health != VpnServerHealth.Unreachable
                             && server.EnabledInboundCount > 0
                             && server.RemainingCapacity > 0
                             && server.Id != service.ServerId)
            .Select(server => (
                server.Id,
                Label: $"{server.Key} — {server.CountryCode} ({server.RemainingCapacity})"))
            .ToList();

        return model;
    }

    private async Task<IActionResult> RedisplayAsync(
        ServiceMigrationCreateViewModel model,
        CancellationToken cancellationToken)
    {
        var reloaded = await PopulateAsync(model, cancellationToken);

        return reloaded is null ? NotFound() : View(reloaded);
    }

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
