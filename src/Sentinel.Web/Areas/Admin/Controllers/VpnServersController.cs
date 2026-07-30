using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Domain.Identity;
using Sentinel.Vpn.Servers;
using Sentinel.Web.Areas.Admin.Models;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Localization;

namespace Sentinel.Web.Areas.Admin.Controllers;

/// <summary>
/// The panels the portal provisions against.
/// <para>
/// Reading is open to back-office read access so support can see whether a server is healthy;
/// every write, and every action that <em>contacts</em> a panel, needs write access. Probing is a
/// write here even though it changes nothing an operator typed: it makes an outbound request with
/// a credential attached, and that is not something a read-only role should be able to trigger.
/// </para>
/// </summary>
[Area("Admin")]
[Authorize(Policy = PolicyNames.BackOfficeRead)]
[Route("Admin/VpnServers")]
public sealed class VpnServersController : Controller
{
    private readonly IVpnServerAdminQuery _query;
    private readonly IVpnServerAdminService _servers;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public VpnServersController(
        IVpnServerAdminQuery query,
        IVpnServerAdminService servers,
        IStringLocalizer<SharedResource> localizer)
    {
        _query = query;
        _servers = servers;
        _localizer = localizer;
    }

    private bool CanWrite => User.IsInRole(RoleNames.SuperAdmin) || User.IsInRole(RoleNames.Admin);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(new VpnServerListViewModel
        {
            Servers = await _query.ListAsync(cancellationToken),
            CanWrite = CanWrite,
            TimeZoneId = UserTime.DefaultTimeZoneId,
        });
    }

    [HttpGet("new")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public IActionResult Create() => View("Edit", new VpnServerEditViewModel());

    [HttpPost("new")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Create(
        VpnServerEditViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View("Edit", model);
        }

        var result = await _servers.SaveAsync(null, model.ToRequest(), cancellationToken);

        if (!result.Succeeded)
        {
            AddError(result);
            return View("Edit", model);
        }

        TempData["StatusMessage"] = _localizer["admin.vpn.saved"].Value;

        // Straight to the inbound allowlist: a server with none cannot take a service, so this is
        // the next thing that has to happen and there is no reason to make an operator find it.
        return RedirectToAction(nameof(Inbounds), new { id = result.Value });
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var server = await _query.GetForEditAsync(id, cancellationToken);

        return server is null ? NotFound() : View(VpnServerEditViewModel.From(server));
    }

    [HttpPost("{id:guid}")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Edit(
        Guid id,
        VpnServerEditViewModel model,
        CancellationToken cancellationToken)
    {
        model.Id = id;

        if (!ModelState.IsValid)
        {
            model.ApiTokenHint = (await _query.GetForEditAsync(id, cancellationToken))?.ApiTokenHint;
            return View(model);
        }

        var result = await _servers.SaveAsync(id, model.ToRequest(), cancellationToken);

        if (!result.Succeeded)
        {
            AddError(result);
            model.ApiTokenHint = (await _query.GetForEditAsync(id, cancellationToken))?.ApiTokenHint;
            return View(model);
        }

        TempData["StatusMessage"] = _localizer["admin.vpn.saved"].Value;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Contacts the panel and records what came back.</summary>
    [HttpPost("{id:guid}/probe")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Probe(Guid id, CancellationToken cancellationToken)
    {
        var result = await _servers.ProbeAsync(id, cancellationToken);

        if (!result.Succeeded)
        {
            TempData["StatusMessage"] = _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;
            return RedirectToAction(nameof(Index));
        }

        var probe = result.Value!;

        TempData["StatusMessage"] = probe.Reachable
            ? _localizer["admin.vpn.probe.ok", probe.InboundCount].Value
            : _localizer["admin.vpn.probe.failed", probe.Error ?? string.Empty].Value;

        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------------- inbounds ----

    [HttpGet("{id:guid}/inbounds")]
    public async Task<IActionResult> Inbounds(Guid id, CancellationToken cancellationToken)
    {
        var server = await _query.GetForEditAsync(id, cancellationToken);

        if (server is null)
        {
            return NotFound();
        }

        return View(new VpnServerInboundsViewModel
        {
            ServerId = id,
            ServerNameFa = server.NameFa,
            ServerNameEn = server.NameEn,
            Allowlisted = await _query.ListInboundsAsync(id, cancellationToken),

            // Not fetched on load: this page is also the one an operator lands on after a save,
            // and reaching out to a panel on every GET would make a read hit the network.
            Discovered = null,
            CanWrite = CanWrite,
        });
    }

    /// <summary>
    /// Asks the panel what inbounds it has.
    /// <para>
    /// A POST, not a GET: it makes an authenticated outbound request, so it must not be something
    /// a link, a prefetch or a crawler can trigger.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/inbounds/discover")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Discover(Guid id, CancellationToken cancellationToken)
    {
        var server = await _query.GetForEditAsync(id, cancellationToken);

        if (server is null)
        {
            return NotFound();
        }

        var discovered = await _servers.DiscoverInboundsAsync(id, cancellationToken);

        return View("Inbounds", new VpnServerInboundsViewModel
        {
            ServerId = id,
            ServerNameFa = server.NameFa,
            ServerNameEn = server.NameEn,
            Allowlisted = await _query.ListInboundsAsync(id, cancellationToken),
            Discovered = discovered.Succeeded ? discovered.Value : null,
            DiscoveryError = discovered.Succeeded
                ? null
                : _localizer[discovered.ErrorKey ?? OperationErrors.NotFound].Value,
            CanWrite = CanWrite,
        });
    }

    [HttpPost("{id:guid}/inbounds/allow")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Allow(
        Guid id,
        int inboundId,
        string? label,
        CancellationToken cancellationToken)
    {
        var result = await _servers.AllowlistInboundAsync(
            id, inboundId, label ?? string.Empty, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.vpn.inbound.added"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Inbounds), new { id });
    }

    [HttpPost("{id:guid}/inbounds/{profileId:guid}/toggle")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> ToggleInbound(
        Guid id,
        Guid profileId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var result = await _servers.SetInboundEnabledAsync(profileId, enabled, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.vpn.saved"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Inbounds), new { id });
    }

    [HttpPost("{id:guid}/inbounds/{profileId:guid}/remove")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> RemoveInbound(
        Guid id,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var result = await _servers.RemoveInboundAsync(profileId, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.vpn.inbound.removed"].Value
            : _localizer[result.ErrorKey ?? OperationErrors.NotFound].Value;

        return RedirectToAction(nameof(Inbounds), new { id });
    }

    private void AddError(OperationResult result) =>
        ModelState.AddModelError(
            string.Empty,
            _localizer[result.ErrorKey ?? OperationErrors.IdentityRejected].Value);
}
