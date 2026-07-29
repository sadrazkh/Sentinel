using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Application.Notifications;
using Sentinel.Web.Localization;

namespace Sentinel.Web.Controllers;

/// <summary>
/// The member's own Telegram link. Every action works on the signed-in account only — there is
/// no user id anywhere in these routes, so there is nothing to tamper with.
/// </summary>
[Authorize(Policy = PolicyNames.ActiveUser)]
public sealed class TelegramController : Controller
{
    private readonly ITelegramLinkService _telegram;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public TelegramController(
        ITelegramLinkService telegram,
        IStringLocalizer<SharedResource> localizer)
    {
        _telegram = telegram;
        _localizer = localizer;
    }

    [HttpPost]
    public async Task<IActionResult> Connect(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var result = await _telegram.CreateInvitationAsync(userId, cancellationToken);

        if (!result.Succeeded || result.Value is null)
        {
            TempData["StatusMessage"] = _localizer[result.ErrorKey ?? TelegramErrors.NotConfigured].Value;
            return RedirectToProfile();
        }

        // Carried through TempData rather than the query string: the deep link contains a
        // single-use token, and a token in a URL ends up in browser history and any proxy log
        // along the way.
        TempData["TelegramDeepLink"] = result.Value.DeepLink;
        TempData["TelegramDeepLinkExpiresAt"] = result.Value.ExpiresAt.ToString("O");

        return RedirectToProfile();
    }

    [HttpPost]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var result = await _telegram.UnlinkAsync(userId, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["telegram.disconnected"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.IdentityRejected].Value;

        return RedirectToProfile();
    }

    [HttpPost]
    public async Task<IActionResult> SetNotifications(bool enabled, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var result = await _telegram.SetNotificationsEnabledAsync(userId, enabled, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer[enabled ? "telegram.notificationsOn" : "telegram.notificationsOff"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.IdentityRejected].Value;

        return RedirectToProfile();
    }

    private IActionResult RedirectToProfile() => RedirectToAction("Index", "Profile");

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
