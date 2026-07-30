using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Web.Localization;

namespace Sentinel.Web.Controllers;

[AllowAnonymous]
public sealed class HomeController : Controller
{
    /// <summary>
    /// The portal's front door.
    /// <para>
    /// A signed-in visitor goes straight to their dashboard — landing on a marketing page when you
    /// already have an account is friction, not welcome. Everyone else gets the landing page
    /// rather than being dropped on a bare sign-in form with no explanation of what they are
    /// signing in to.
    /// </para>
    /// </summary>
    [HttpGet("/")]
    public IActionResult Index() =>
        User.Identity?.IsAuthenticated == true
            ? RedirectToAction("Index", "Dashboard")
            : View();

    /// <summary>
    /// Switches the interface language. A POST with an anti-forgery token, because it writes
    /// a cookie — a GET would let a third-party page flip a visitor's language.
    /// </summary>
    [HttpPost("/language")]
    [ValidateAntiForgeryToken]
    public IActionResult SetLanguage(string culture, string? returnUrl)
    {
        // Allow-list only: the value ends up in a cookie the localisation middleware parses.
        if (!PortalCultures.IsSupported(culture))
        {
            return BadRequest();
        }

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                Path = "/",
            });

        return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
    }
}
