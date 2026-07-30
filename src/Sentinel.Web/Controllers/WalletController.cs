using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Authorization;
using Sentinel.Application.Billing;
using Sentinel.Application.Features;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Models.Wallet;
using Sentinel.Web.Security;

namespace Sentinel.Web.Controllers;

/// <summary>
/// A member's own credit.
/// <para>
/// <b>Read-only, entirely and deliberately.</b> There is one action here and it is a GET. This
/// portal has no top-up page, no payment gateway and no member-facing path of any kind that raises a
/// balance — credit is added by an operator, from the back office, with their name recorded against
/// it. The absence of a POST on this controller is the mechanism, not an oversight; a future version
/// that adds one is changing a security boundary and should be reviewed as such.
/// </para>
/// </summary>
[Authorize(Policy = PolicyNames.ActiveUser)]
[Route("wallet")]
[RequireFeature(FeatureNames.Wallet)]
public sealed class WalletController : Controller
{
    private const int StatementLength = 50;

    private readonly IWalletService _wallet;

    public WalletController(IWalletService wallet) => _wallet = wallet;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Forbid();
        }

        // Scoped to the signed-in member by the claim, never by a route or query value: there is no
        // parameter here that could be changed to read somebody else's statement.
        var created = await _wallet.GetOrCreateAsync(userId, cancellationToken);

        if (!created.Succeeded)
        {
            return NotFound();
        }

        var ledger = await _wallet.GetLedgerAsync(userId, StatementLength, cancellationToken);

        return View(new WalletPageViewModel
        {
            Ledger = ledger,
            TimeZoneId = UserTime.DefaultTimeZoneId,
        });
    }
}
