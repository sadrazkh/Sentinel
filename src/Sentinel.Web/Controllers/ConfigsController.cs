using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Sentinel.Application.Accounts;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Application.Subscriptions;
using Sentinel.Infrastructure.Subscriptions;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Localization;
using Sentinel.Web.Models.Configs;

namespace Sentinel.Web.Controllers;

/// <summary>
/// The member's subscription configs.
/// <para>
/// Every action is scoped to the signed-in account — there is no subscription id in any route
/// that is not also checked against its owner, and the service applies that check again in SQL.
/// </para>
/// </summary>
[Authorize(Policy = PolicyNames.ActiveUser)]
public sealed class ConfigsController : Controller
{
    private readonly ISubscriptionService _subscriptions;
    private readonly IProfileService _profile;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly SubscriptionFetchOptions _options;

    public ConfigsController(
        ISubscriptionService subscriptions,
        IProfileService profile,
        IStringLocalizer<SharedResource> localizer,
        IOptions<SubscriptionFetchOptions> options)
    {
        _subscriptions = subscriptions;
        _profile = profile;
        _localizer = localizer;
        _options = options.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var subscriptions = await _subscriptions.GetForUserAsync(
            userId, forceRefresh: false, cancellationToken);

        var profile = await _profile.GetAsync(userId, cancellationToken);

        return View(new ConfigsViewModel
        {
            Subscriptions = subscriptions,
            TimeZoneId = profile?.TimeZoneId ?? UserTime.DefaultTimeZoneId,
            CanAddOwn = _options.Enabled,
            MaxSources = _options.MaxSourcesPerUser,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Add(AddSubscriptionViewModel model, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = _localizer[SubscriptionErrors.InvalidUrl].Value;
            return RedirectToAction(nameof(Index));
        }

        var result = await _subscriptions.AddAsync(
            userId,
            new SaveSubscriptionRequest(model.Title ?? "Subscription", model.Url, true, null, null),
            cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["configs.added"].Value
            : _localizer[result.ErrorKey ?? SubscriptionErrors.InvalidUrl].Value;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Refresh(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        // Ownership is enforced inside the service, so a guessed id belonging to another
        // member simply reports "not found".
        var result = await _subscriptions.RefreshAsync(userId, id, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["configs.refreshed"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var result = await _subscriptions.RemoveAsync(userId, id, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["configs.removed"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Index));
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
