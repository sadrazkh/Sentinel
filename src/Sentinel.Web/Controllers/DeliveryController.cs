using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Sentinel.Vpn.Delivery;
using Sentinel.Web.Security;

namespace Sentinel.Web.Controllers;

/// <summary>
/// Serves a member's VPN configurations from a short, unauthenticated URL.
/// <para>
/// Unauthenticated on purpose and by necessity: a VPN client application polls a subscription URL and
/// has no way to sign in. The token in the path is therefore the entire authorisation — 256 bits of
/// randomness, stored only as a hash, and revocable by rotating it.
/// </para>
/// <para>
/// The path is deliberately short and unbranded (<c>/s/{token}</c>) so it fits on a QR code and in a
/// client's configuration field, and so it does not announce which portal it belongs to.
/// </para>
/// </summary>
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Delivery)]
public sealed class DeliveryController : Controller
{
    private readonly IDeliveryService _delivery;
    private readonly ILogger<DeliveryController> _logger;

    public DeliveryController(IDeliveryService delivery, ILogger<DeliveryController> logger)
    {
        _delivery = delivery;
        _logger = logger;
    }

    /// <summary>
    /// The subscription form: one base64 blob of URIs, which is what client applications expect.
    /// </summary>
    [HttpGet("/s/{token}")]
    public Task<IActionResult> Subscription(string token, CancellationToken cancellationToken) =>
        DeliverAsync(token, DeliveryFormat.Subscription, cancellationToken);

    /// <summary>
    /// The plain form: newline-separated URIs, for a person reading them rather than an application.
    /// </summary>
    [HttpGet("/s/{token}/plain")]
    public Task<IActionResult> Plain(string token, CancellationToken cancellationToken) =>
        DeliverAsync(token, DeliveryFormat.Plain, cancellationToken);

    private async Task<IActionResult> DeliverAsync(
        string token,
        DeliveryFormat format,
        CancellationToken cancellationToken)
    {
        // No branded error page on this path. The application re-executes /error/{code} for every
        // failing status, which would answer an anonymous probe with a full portal page — naming the
        // portal, in the member's language, from a URL whose entire design is to say nothing about
        // where it leads. A client application cannot read it either. So the status code stands alone.
        var statusCodePages = HttpContext.Features.Get<IStatusCodePagesFeature>();

        if (statusCodePages is not null)
        {
            statusCodePages.Enabled = false;
        }

        var result = await _delivery.DeliverAsync(token, format, cancellationToken);

        // Never the whole token — this line goes to a log sink.
        var fingerprint = DeliveryToken.Fingerprint(token);

        switch (result.Outcome)
        {
            case DeliveryOutcome.Delivered:
                // No caching anywhere. This body is a member's own credentials, and a shared cache
                // holding it would serve one customer's configurations to the next requester.
                Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, private";
                Response.Headers.Pragma = "no-cache";

                // Client applications read this to name the profile they create.
                Response.Headers["Profile-Update-Interval"] = "12";

                _logger.LogInformation(
                    "Delivered {Count} configuration(s) for link {Fingerprint}.",
                    result.ConfigCount,
                    fingerprint);

                // text/plain, not an attachment: client applications fetch this URL directly and a
                // download disposition would make a browser save it instead of showing it.
                return Content(result.Body!, "text/plain; charset=utf-8");

            case DeliveryOutcome.NotUsable:
                // 410 rather than 404. The holder of a valid token already knows the service exists,
                // so telling them it has lapsed is useful — and a client application treats Gone as
                // "stop polling" rather than "retry for ever".
                _logger.LogInformation("Link {Fingerprint} is no longer usable.", fingerprint);

                return StatusCode(StatusCodes.Status410Gone, "This service is no longer active.");

            case DeliveryOutcome.Unavailable:
                // 503 with Retry-After: the service is fine, the panel is not. A client should come
                // back rather than conclude its subscription is dead.
                Response.Headers.RetryAfter = "300";

                _logger.LogWarning(
                    "Link {Fingerprint} could not be served: the panel was unavailable.", fingerprint);

                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable.");

            default:
                // Unknown, malformed and revoked are one answer. Distinguishing them would turn this
                // endpoint into a way of learning which tokens exist.
                //
                // Not logged at information level: an unauthenticated endpoint receiving junk is
                // ordinary, and logging each one is how a log becomes an attacker's write primitive.
                _logger.LogDebug("Link {Fingerprint} did not match any service.", fingerprint);

                return NotFound();
        }
    }
}
