using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Authorization;
using Sentinel.Application.Features;
using Sentinel.Web.Areas.Admin.Models;
using Sentinel.Web.Localization;

namespace Sentinel.Web.Areas.Admin.Controllers;

/// <summary>
/// The switchboard: which areas of the portal are open, and to everyone at once.
/// <para>
/// Its own page rather than a section of System, which is deliberately read-only. The distinction
/// is what a screen is allowed to write: a feature is an operating decision somebody takes with the
/// system in front of them, whereas a connection string or a key belongs to the deployment and must
/// not have a second, mutable source of truth.
/// </para>
/// <para>
/// Restricted to system administration — the same policy as the system page. Turning a feature on
/// changes what every member sees, and turning the financial ones on lets money move.
/// </para>
/// </summary>
[Area("Admin")]
[Authorize(Policy = PolicyNames.SystemAdministration)]
[Route("Admin/Features")]
public sealed class FeaturesController : Controller
{
    private readonly IFeatureAdminService _features;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public FeaturesController(
        IFeatureAdminService features,
        IStringLocalizer<SharedResource> localizer)
    {
        _features = features;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(new FeatureListViewModel
        {
            Features = await _features.ListAsync(cancellationToken),
        });

    /// <summary>
    /// Moves one switch.
    /// <para>
    /// The feature name comes from the route and is matched against the names this build actually
    /// has, so a crafted post cannot create a row for an invented one.
    /// </para>
    /// </summary>
    [HttpPost("{name}")]
    public async Task<IActionResult> Set(
        [FromRoute] string name,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperatorId(out var operatorId))
        {
            return Forbid();
        }

        var result = await _features.SetAsync(name, enabled, operatorId, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer[enabled ? "admin.feature.turnedOn" : "admin.feature.turnedOff"].Value
            : _localizer[result.ErrorKey ?? FeatureErrors.UnknownFeature].Value;

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Hands a switch back to whatever the deployment configured.</summary>
    [HttpPost("{name}/reset")]
    public async Task<IActionResult> Reset(
        [FromRoute] string name,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperatorId(out var operatorId))
        {
            return Forbid();
        }

        var result = await _features.SetAsync(name, null, operatorId, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.feature.reset"].Value
            : _localizer[result.ErrorKey ?? FeatureErrors.UnknownFeature].Value;

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Opens the whole VPN flow in one action: members manage their own services, hold credit, and
    /// order plans with it.
    /// <para>
    /// A single button because the three are useless apart — credit nobody can spend, or an order
    /// button with nothing behind it. It is still three separate switches underneath, so each can
    /// be turned off on its own afterwards.
    /// </para>
    /// </summary>
    [HttpPost("open-vpn")]
    public async Task<IActionResult> OpenVpn(CancellationToken cancellationToken)
    {
        if (!TryGetOperatorId(out var operatorId))
        {
            return Forbid();
        }

        string[] bundle =
        [
            FeatureNames.VpnSelfService,
            FeatureNames.Wallet,
            FeatureNames.Purchases,
        ];

        foreach (var feature in bundle)
        {
            var result = await _features.SetAsync(feature, true, operatorId, cancellationToken);

            if (!result.Succeeded)
            {
                TempData["StatusMessage"] =
                    _localizer[result.ErrorKey ?? FeatureErrors.UnknownFeature].Value;

                return RedirectToAction(nameof(Index));
            }
        }

        TempData["StatusMessage"] = _localizer["admin.feature.vpnOpened"].Value;

        return RedirectToAction(nameof(Index));
    }

    private bool TryGetOperatorId(out Guid operatorId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out operatorId);
}
