using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Accounts;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Application.Notifications;
using Sentinel.Web.Localization;
using Sentinel.Web.Models.Account;
using Sentinel.Web.Services;

namespace Sentinel.Web.Controllers;

[Authorize(Policy = PolicyNames.ActiveUser)]
public sealed class ProfileController : Controller
{
    /// <summary>
    /// Offered in the picker. A short curated list beats every zone the host happens to know:
    /// the value is validated against the system database anyway, so this is presentation.
    /// </summary>
    private static readonly string[] SuggestedTimeZones =
    [
        "Asia/Tehran",
        "Asia/Dubai",
        "Asia/Istanbul",
        "Europe/London",
        "Europe/Berlin",
        "America/New_York",
        "America/Los_Angeles",
        "UTC",
    ];

    private readonly IProfileService _profile;
    private readonly IPortalSignInService _signInService;
    private readonly ITelegramLinkService _telegram;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ProfileController(
        IProfileService profile,
        IPortalSignInService signInService,
        ITelegramLinkService telegram,
        IStringLocalizer<SharedResource> localizer)
    {
        _profile = profile;
        _signInService = signInService;
        _telegram = telegram;
        _localizer = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // No id in the route: a member edits their own profile and nothing else, so there is
        // no identifier for a caller to substitute.
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var profile = await _profile.GetAsync(userId, cancellationToken);

        if (profile is null)
        {
            return Forbid();
        }

        var model = ProfileViewModel.From(profile);
        model.TimeZoneOptions = BuildTimeZoneOptions(profile.TimeZoneId);
        model.Telegram = await _telegram.GetStateAsync(userId, cancellationToken);

        // Shown once, straight after the connect request. The link carries a single-use token,
        // so it is never persisted into the page's own URL.
        model.TelegramDeepLink = TempData["TelegramDeepLink"] as string;

        if (TempData["TelegramDeepLinkExpiresAt"] is string expiresAt
            && DateTimeOffset.TryParse(expiresAt, out var parsed))
        {
            model.TelegramDeepLinkExpiresAt = parsed;
        }

        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Index(ProfileViewModel model, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        model.TimeZoneOptions = BuildTimeZoneOptions(model.TimeZoneId);

        if (!ModelState.IsValid)
        {
            await RestoreReadOnlyFieldsAsync(model, userId, cancellationToken);
            return View(model);
        }

        var result = await _profile.UpdateAsync(userId, model.ToRequest(), cancellationToken);

        if (!result.Succeeded)
        {
            ModelState.AddModelError(
                string.Empty, _localizer[result.ErrorKey ?? OperationErrors.IdentityRejected].Value);

            await RestoreReadOnlyFieldsAsync(model, userId, cancellationToken);
            return View(model);
        }

        // The saved preference becomes the active one immediately, so the redirect below
        // already renders in the newly chosen language.
        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(
                new RequestCulture(model.PreferredCulture == "en"
                    ? PortalCultures.English
                    : PortalCultures.Persian)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                Path = "/",
            });

        // The display name lives in the authentication cookie's claims, so the cookie is
        // re-issued; otherwise the header would keep showing the old name until sign-out.
        // The session-id claim is preserved, which Identity's RefreshSignInAsync would drop.
        await _signInService.RefreshSessionAsync(cancellationToken);

        TempData["StatusMessage"] = _localizer["profile.saved"].Value;
        return RedirectToAction(nameof(Index));
    }

    private async Task RestoreReadOnlyFieldsAsync(
        ProfileViewModel model,
        Guid userId,
        CancellationToken cancellationToken)
    {
        // Username, e-mail and the Telegram state are display-only; they are never bound from
        // the form, so they have to be re-read rather than trusted from the post.
        if (await _profile.GetAsync(userId, cancellationToken) is { } current)
        {
            model.UserName = current.UserName;
            model.Email = current.Email;
        }

        model.Telegram = await _telegram.GetStateAsync(userId, cancellationToken);
    }

    private static IReadOnlyList<string> BuildTimeZoneOptions(string? current)
    {
        if (string.IsNullOrWhiteSpace(current) || SuggestedTimeZones.Contains(current))
        {
            return SuggestedTimeZones;
        }

        // Keep whatever the account already has, so opening the page cannot silently change it.
        return [current, .. SuggestedTimeZones];
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
