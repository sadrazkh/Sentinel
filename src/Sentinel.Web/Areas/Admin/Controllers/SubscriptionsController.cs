using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Application.Subscriptions;
using Sentinel.Domain.Identity;
using Sentinel.Web.Areas.Admin.Models;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Localization;

namespace Sentinel.Web.Areas.Admin.Controllers;

/// <summary>
/// Operator view of every subscription, built around one job: finding the dead ones — expired,
/// out of quota, or failing to fetch — and removing them so members' pages stay useful.
/// <para>
/// The subscription URL is never listed here. It is the credential that retrieves a member's
/// configs, and an operator has no need to read it in order to decide whether a source is dead.
/// </para>
/// </summary>
[Area("Admin")]
[Authorize(Policy = PolicyNames.BackOfficeRead)]
public sealed class SubscriptionsController : Controller
{
    private readonly ISubscriptionAdminService _subscriptions;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly TimeProvider _timeProvider;

    public SubscriptionsController(
        ISubscriptionAdminService subscriptions,
        IStringLocalizer<SharedResource> localizer,
        TimeProvider timeProvider)
    {
        _subscriptions = subscriptions;
        _localizer = localizer;
        _timeProvider = timeProvider;
    }

    private bool CanWrite => User.IsInRole(RoleNames.SuperAdmin) || User.IsInRole(RoleNames.Admin);

    [HttpGet]
    public async Task<IActionResult> Index(
        string? search,
        bool onlyDead = false,
        int page = 1,
        int pageSize = PagingDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var results = await _subscriptions.SearchAsync(search, onlyDead, page, pageSize, cancellationToken);

        return View(new SubscriptionAdminViewModel
        {
            Results = results,
            Search = search,
            OnlyDead = onlyDead,
            CanWrite = CanWrite,
            TimeZoneId = UserTime.DefaultTimeZoneId,
            Now = _timeProvider.GetUtcNow(),
        });
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Delete(
        Guid id,
        string? search,
        bool onlyDead,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptions.DeleteAsync(id, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.subscriptions.deleted"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Index), new { search, onlyDead });
    }

    /// <summary>
    /// Removes every dead source in one pass. Guarded by a typed confirmation because it
    /// deletes across all members at once and cannot be undone.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> PurgeDead(string? confirmation, CancellationToken cancellationToken)
    {
        if (!string.Equals(confirmation, "PURGE", StringComparison.OrdinalIgnoreCase))
        {
            TempData["StatusMessage"] = _localizer["admin.subscriptions.purgeConfirmRequired"].Value;
            return RedirectToAction(nameof(Index), new { onlyDead = true });
        }

        var removed = await _subscriptions.DeleteDeadAsync(cancellationToken);

        TempData["StatusMessage"] = _localizer["admin.subscriptions.purged", removed].Value;
        return RedirectToAction(nameof(Index), new { onlyDead = true });
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> AddForUser(
        AddSubscriptionForUserViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = _localizer[SubscriptionErrors.InvalidUrl].Value;
            return RedirectToUser(model.UserId);
        }

        // The same validation as the self-service path: an operator's URL is no more trusted,
        // because the server that would be reached is the same either way.
        var result = await _subscriptions.AddForUserAsync(
            model.UserId,
            new SaveSubscriptionRequest(
                model.Title ?? "Subscription", model.Url, true, model.Notes, null),
            cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.subscriptions.added"].Value
            : _localizer[result.ErrorKey ?? SubscriptionErrors.InvalidUrl].Value;

        return RedirectToUser(model.UserId);
    }

    private IActionResult RedirectToUser(Guid userId) =>
        RedirectToAction("Details", "Users", new { id = userId });
}
