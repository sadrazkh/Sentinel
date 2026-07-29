using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Accounts;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Application.Notifications;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Models.Notifications;

namespace Sentinel.Web.Controllers;

[Authorize(Policy = PolicyNames.ActiveUser)]
public sealed class NotificationsController : Controller
{
    private readonly INotificationService _notifications;
    private readonly IProfileService _profile;

    public NotificationsController(INotificationService notifications, IProfileService profile)
    {
        _notifications = notifications;
        _profile = profile;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        int page = 1,
        int pageSize = PagingDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        // Scoped by the authenticated principal. There is no user id in the route, so there is
        // nothing to change in the URL to read somebody else's messages.
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var notifications = await _notifications.GetForUserAsync(userId, page, pageSize, cancellationToken);
        var profile = await _profile.GetAsync(userId, cancellationToken);

        return View(new NotificationsViewModel
        {
            Notifications = notifications,
            TimeZoneId = profile?.TimeZoneId ?? UserTime.DefaultTimeZoneId,
        });
    }

    [HttpPost]
    public async Task<IActionResult> MarkRead(
        Guid id,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        // The service scopes the update by user id too, so a guessed notification id belonging
        // to another member changes nothing and reports the same "not found".
        await _notifications.MarkReadAsync(userId, id, cancellationToken);

        return RedirectToLocalOr(returnUrl, nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        await _notifications.MarkAllReadAsync(userId, cancellationToken);

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Marks a notification read and follows its link in one step, so a member does not have to
    /// do both by hand.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Open(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var page = await _notifications.GetForUserAsync(userId, 1, PagingDefaults.MaxPageSize, cancellationToken);
        var notification = page.Items.FirstOrDefault(n => n.Id == id);

        if (notification is null)
        {
            return NotFound();
        }

        await _notifications.MarkReadAsync(userId, id, cancellationToken);

        // The stored path was constrained to a local one when the notification was written;
        // it is checked again here, because this is where the redirect actually happens.
        return RedirectToLocalOr(notification.LinkPath, nameof(Index));
    }

    private IActionResult RedirectToLocalOr(string? candidate, string fallbackAction) =>
        !string.IsNullOrWhiteSpace(candidate) && Url.IsLocalUrl(candidate)
            ? LocalRedirect(candidate)
            : RedirectToAction(fallbackAction);

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
