using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sentinel.Application.Authorization;
using Sentinel.Application.Billing;
using Sentinel.Application.Features;
using Sentinel.Application.Users;
using Sentinel.Domain.Identity;
using Sentinel.Web.Areas.Admin.Models;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Localization;
using Sentinel.Web.Security;

namespace Sentinel.Web.Areas.Admin.Controllers;

/// <summary>
/// A member's credit, and the only place it can be changed.
/// <para>
/// This controller is the entire write surface of the wallet. There is no member-facing counterpart
/// — no top-up page, no payment callback, no endpoint anywhere else that raises a balance. Credit
/// enters one way, an operator puts it there, and it is recorded with their name against it.
/// </para>
/// <para>
/// Reading is open to back-office read access so support can answer "what did I pay for"; every
/// movement needs write access.
/// </para>
/// </summary>
[Area("Admin")]
[Authorize(Policy = PolicyNames.BackOfficeRead)]
[Route("Admin/Wallets")]
[RequireFeature(FeatureNames.Wallet)]
public sealed class WalletsController : Controller
{
    private readonly IWalletService _wallet;
    private readonly IUserAdminQuery _users;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public WalletsController(
        IWalletService wallet,
        IUserAdminQuery users,
        IStringLocalizer<SharedResource> localizer)
    {
        _wallet = wallet;
        _users = users;
        _localizer = localizer;
    }

    private bool CanWrite => User.IsInRole(RoleNames.SuperAdmin) || User.IsInRole(RoleNames.Admin);

    /// <summary>
    /// Every member and what they hold.
    /// <para>
    /// The way in for the common task — "put credit on this account" — which previously meant
    /// finding the member first and knowing the button was on their page.
    /// </para>
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(string? search, CancellationToken cancellationToken) =>
        View(new WalletListViewModel
        {
            Holders = await _wallet.ListHoldersAsync(search, 50, cancellationToken),
            Search = search,
            CanWrite = CanWrite,
        });

    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Details([FromRoute] Guid userId, CancellationToken cancellationToken)
    {
        var member = await _users.GetDetailAsync(userId, cancellationToken);

        if (member is null)
        {
            return NotFound();
        }

        // Creates an empty wallet on first look, so an operator never meets "this member has no
        // wallet" as a state they have to resolve before they can credit one.
        var created = await _wallet.GetOrCreateAsync(userId, cancellationToken);

        if (!created.Succeeded)
        {
            return NotFound();
        }

        var ledger = await _wallet.GetLedgerAsync(userId, 100, cancellationToken);

        return View(new WalletDetailViewModel
        {
            UserId = userId,
            UserName = member.UserName,
            DisplayName = member.DisplayName,
            Ledger = ledger,
            CanWrite = CanWrite,
            TimeZoneId = UserTime.DefaultTimeZoneId,
        });
    }

    [HttpPost("{userId:guid}/credit")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public Task<IActionResult> Credit(
        [FromRoute] Guid userId,
        WalletAdjustViewModel model,
        CancellationToken cancellationToken) =>
        AdjustAsync(userId, model, credit: true, cancellationToken);

    [HttpPost("{userId:guid}/debit")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public Task<IActionResult> Debit(
        [FromRoute] Guid userId,
        WalletAdjustViewModel model,
        CancellationToken cancellationToken) =>
        AdjustAsync(userId, model, credit: false, cancellationToken);

    /// <summary>
    /// Appends the opposite of an earlier entry. The only correction this ledger has — nothing here
    /// edits or removes a row.
    /// </summary>
    [HttpPost("{userId:guid}/reverse/{transactionId:guid}")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Reverse(
        [FromRoute] Guid userId,
        [FromRoute] Guid transactionId,
        string? description,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperatorId(out var operatorId))
        {
            return Forbid();
        }

        var result = await _wallet.ReverseAsync(
            transactionId, operatorId, description, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer["admin.wallet.reversed"].Value
            : _localizer[result.ErrorKey ?? WalletErrors.EntryNotFound].Value;

        return RedirectToAction(nameof(Details), new { userId });
    }

    [HttpPost("{userId:guid}/freeze")]
    [Authorize(Policy = PolicyNames.BackOfficeWrite)]
    public async Task<IActionResult> Freeze(
        [FromRoute] Guid userId,
        bool frozen,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperatorId(out var operatorId))
        {
            return Forbid();
        }

        var result = await _wallet.SetFrozenAsync(
            userId, frozen, reason, operatorId, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer[frozen ? "admin.wallet.froze" : "admin.wallet.unfroze"].Value
            : _localizer[result.ErrorKey ?? WalletErrors.NotFound].Value;

        return RedirectToAction(nameof(Details), new { userId });
    }

    // -------------------------------------------------------------------------- helpers ----

    private async Task<IActionResult> AdjustAsync(
        Guid userId,
        WalletAdjustViewModel model,
        bool credit,
        CancellationToken cancellationToken)
    {
        if (!TryGetOperatorId(out var operatorId))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = _localizer[WalletErrors.AmountInvalid].Value;
            return RedirectToAction(nameof(Details), new { userId });
        }

        // The route's id, not the form's: the amount is the only thing this form decides.
        var request = new AdjustWalletRequest(
            userId, model.AmountMinorUnits, model.Description, model.Reference);

        var result = credit
            ? await _wallet.CreditAsync(request, operatorId, cancellationToken)
            : await _wallet.DebitAsync(request, operatorId, cancellationToken);

        TempData["StatusMessage"] = result.Succeeded
            ? _localizer[credit ? "admin.wallet.credited" : "admin.wallet.debited"].Value
            : _localizer[result.ErrorKey ?? WalletErrors.AmountInvalid].Value;

        return RedirectToAction(nameof(Details), new { userId });
    }

    /// <summary>
    /// The signed-in operator, recorded against every movement.
    /// <para>
    /// Read from the principal, never from the form. An operator id a request could set would let
    /// somebody attribute their own adjustment to a colleague.
    /// </para>
    /// </summary>
    private bool TryGetOperatorId(out Guid operatorId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out operatorId);
}
