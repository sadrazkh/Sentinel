using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Authorization;
using Sentinel.Web.Localization;
using Sentinel.Web.Models.Account;
using Sentinel.Web.Security;
using Sentinel.Web.Services;

namespace Sentinel.Web.Controllers;

// Anonymous access is granted per action, never on the controller: a controller-level
// [AllowAnonymous] silently overrides [Authorize] on the actions beneath it, which would have
// left the sign-out endpoints reachable without a session.
public sealed class AccountController : Controller
{
    private readonly IPortalSignInService _signInService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public AccountController(IPortalSignInService signInService, IStringLocalizer<SharedResource> localizer)
    {
        _signInService = signInService;
        _localizer = localizer;
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocalOrDashboard(returnUrl);
        }

        return View(new LoginViewModel { ReturnUrl = Sanitize(returnUrl) });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken cancellationToken)
    {
        model.ReturnUrl = Sanitize(model.ReturnUrl);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var outcome = await _signInService.SignInAsync(
            new SignInRequest(model.Identifier, model.Password, model.RememberMe),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            // One message for every failure mode. Distinguishing "no such user" from "wrong
            // password" — or naming a lockout — would turn this form into an account oracle.
            ModelState.AddModelError(string.Empty, _localizer["login.error.invalidCredentials"]);
            return View(model);
        }

        return RedirectToLocalOrDashboard(model.ReturnUrl);
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await _signInService.SignOutAsync(cancellationToken);
        return RedirectToAction(nameof(Login));
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.ActiveUser)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LogoutEverywhere(CancellationToken cancellationToken)
    {
        await _signInService.SignOutEverywhereAsync(cancellationToken);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult AccessDenied(string? returnUrl = null)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        ViewData["AttemptedPath"] = Sanitize(returnUrl);
        return View();
    }

    private IActionResult RedirectToLocalOrDashboard(string? returnUrl) =>
        Sanitize(returnUrl) is { } local
            ? LocalRedirect(local)
            : RedirectToAction("Index", "Dashboard");

    /// <summary>
    /// Open-redirect guard. <see cref="IUrlHelper.IsLocalUrl"/> rejects absolute URLs as well
    /// as the protocol-relative and backslash tricks (<c>//evil.example</c>, <c>/\evil.example</c>)
    /// that a naive "starts with /" check would wave through.
    /// </summary>
    private string? Sanitize(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null;
}
