using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Accounts;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Application.Security;
using Sentinel.Domain.Security;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Localization;
using Sentinel.Web.Models.Account;
using Sentinel.Web.Security;
using Sentinel.Web.Services;

namespace Sentinel.Web.Controllers;

[Authorize(Policy = PolicyNames.ActiveUser)]
public sealed class SecurityController : Controller
{
    private readonly IProfileService _profile;
    private readonly IUserSessionService _sessions;
    private readonly IClientContext _clientContext;
    private readonly IPortalSignInService _signInService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly SentinelSecurityOptions _securityOptions;

    public SecurityController(
        IProfileService profile,
        IUserSessionService sessions,
        IClientContext clientContext,
        IPortalSignInService signInService,
        IStringLocalizer<SharedResource> localizer,
        IOptions<SentinelSecurityOptions> securityOptions)
    {
        _profile = profile;
        _sessions = sessions;
        _clientContext = clientContext;
        _signInService = signInService;
        _localizer = localizer;
        _securityOptions = securityOptions.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        return View(await BuildViewModelAsync(userId, cancellationToken));
    }

    [HttpPost]
    public async Task<IActionResult> ChangePassword(
        ChangePasswordViewModel model,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            return await RenderWithErrorsAsync(userId, model, cancellationToken);
        }

        var result = await _profile.ChangePasswordAsync(
            userId, new ChangePasswordRequest(model.CurrentPassword, model.NewPassword), cancellationToken);

        if (!result.Succeeded)
        {
            ModelState.AddModelError(
                string.Empty, _localizer[result.ErrorKey ?? OperationErrors.PasswordRejected].Value);

            foreach (var detail in result.Details)
            {
                ModelState.AddModelError(string.Empty, detail);
            }

            return await RenderWithErrorsAsync(userId, model, cancellationToken);
        }

        // A password change is a security event, so every other device is signed out. The
        // current one is kept: forcing the person who just proved their identity to log in
        // again teaches nothing and only tempts them to pick a weaker password next time.
        var currentSessionId = _clientContext.SessionId;

        var revoked = await _sessions.RevokeAllForUserAsync(
            userId, SessionRevocationReason.PasswordChanged, currentSessionId, cancellationToken);

        // The security stamp has already rotated, which would otherwise invalidate this
        // browser's own cookie on its next request. The re-issue keeps the session-id claim,
        // which Identity's own RefreshSignInAsync would drop.
        await _signInService.RefreshSessionAsync(cancellationToken);

        TempData["StatusMessage"] = revoked > 0
            ? _localizer["security.passwordChanged.otherSessionsEnded", revoked].Value
            : _localizer["security.passwordChanged"].Value;

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Ends one other session, leaving this browser signed in.</summary>
    [HttpPost]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        // The session id comes from the form, so ownership is verified rather than assumed:
        // without this check any member could revoke anybody's session by guessing an id.
        var sessions = await _sessions.ListActiveAsync(userId, _clientContext.SessionId, cancellationToken);

        if (sessions.All(session => session.Id != sessionId))
        {
            return NotFound();
        }

        await _sessions.RevokeAsync(sessionId, SessionRevocationReason.UserLogout, cancellationToken);

        TempData["StatusMessage"] = _localizer["security.sessionRevoked"].Value;
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> RenderWithErrorsAsync(
        Guid userId,
        ChangePasswordViewModel model,
        CancellationToken cancellationToken)
    {
        var viewModel = await BuildViewModelAsync(userId, cancellationToken);

        // The submitted passwords are deliberately not echoed back into the form.
        viewModel.ChangePassword = new ChangePasswordViewModel();

        _ = model;
        return View(nameof(Index), viewModel);
    }

    private async Task<SecurityViewModel> BuildViewModelAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var sessions = await _sessions.ListActiveAsync(userId, _clientContext.SessionId, cancellationToken);
        var profile = await _profile.GetAsync(userId, cancellationToken);
        var password = _securityOptions.Password;

        return new SecurityViewModel
        {
            Sessions = sessions,
            TimeZoneId = profile?.TimeZoneId ?? UserTime.DefaultTimeZoneId,
            PasswordMinimumLength = password.MinimumLength,
            PasswordRequiresDigit = password.RequireDigit,
            PasswordRequiresUppercase = password.RequireUppercase,
            PasswordRequiresNonAlphanumeric = password.RequireNonAlphanumeric,
        };
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
