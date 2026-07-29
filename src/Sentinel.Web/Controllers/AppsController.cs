using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Access;
using Sentinel.Application.Accounts;
using Sentinel.Application.Auditing;
using Sentinel.Application.Authorization;
using Sentinel.Application.Catalog;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Catalog;
using Sentinel.Web.Infrastructure;
using Sentinel.Web.Models.Apps;

namespace Sentinel.Web.Controllers;

[Authorize(Policy = PolicyNames.ActiveUser)]
public sealed class AppsController : Controller
{
    private readonly IAccessDecisionService _accessDecisions;
    private readonly IAccountOverviewQuery _accountOverview;
    private readonly IAuditService _audit;
    private readonly ILogger<AppsController> _logger;

    public AppsController(
        IAccessDecisionService accessDecisions,
        IAccountOverviewQuery accountOverview,
        IAuditService audit,
        ILogger<AppsController> logger)
    {
        _accessDecisions = accessDecisions;
        _accountOverview = accountOverview;
        _audit = audit;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var catalog = await _accessDecisions.GetCatalogAsync(userId, cancellationToken);
        var overview = await _accountOverview.GetAsync(userId, 1, cancellationToken);

        return View(new AppsIndexViewModel
        {
            Membership = catalog.Membership,
            Applications = catalog.Applications,
            TimeZoneId = overview?.TimeZoneId ?? UserTime.DefaultTimeZoneId,
            AccessibleCount = catalog.AccessibleCount,
            LockedCount = catalog.LockedCount,
        });
    }

    /// <summary>
    /// Opens an application.
    /// <para>
    /// The client never holds the destination URL: it asks the portal to open an application
    /// by key, the decision is made here, and only then does a redirect happen. That is what
    /// makes the lock on a card a real control rather than a hidden button — and it gives one
    /// reliable place to record who opened what.
    /// </para>
    /// </summary>
    [HttpGet("/apps/{key}/open")]
    public async Task<IActionResult> Open(string key, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Forbid();
        }

        var resolution = await _accessDecisions.ResolveLaunchAsync(userId, key, cancellationToken);

        if (resolution is null)
        {
            return NotFound();
        }

        if (!resolution.Decision.IsAllowed)
        {
            await _audit.RecordAndSaveAsync(
                AuditEntry.For(
                    AuditActions.ApplicationLaunchDenied,
                    nameof(PortalApplication),
                    resolution.ApplicationId) with
                {
                    Result = AuditResult.Denied,
                    Metadata = AuditMetadata.Create()
                        .Set("applicationKey", resolution.ApplicationKey)
                        .Set("reason", resolution.Decision.Reason.ToString()),
                },
                cancellationToken);

            Response.StatusCode = StatusCodes.Status403Forbidden;

            return View("LaunchDenied", new LaunchDeniedViewModel
            {
                ApplicationName = resolution.ApplicationName,
                Reason = resolution.Decision.Reason,
            });
        }

        // Re-validated even though an administrator already had to pass the same check when
        // saving. This is the moment the browser is told to follow the value, so a row that
        // predates the rule — or arrived via a database restore — must not be trusted here.
        if (!ApplicationUrlPolicy.IsAllowed(resolution.LaunchUrl))
        {
            _logger.LogError(
                "Application {ApplicationKey} has a launch URL that fails the URL policy; refusing to redirect.",
                resolution.ApplicationKey);

            await _audit.RecordAndSaveAsync(
                AuditEntry.For(
                    AuditActions.ApplicationLaunchDenied,
                    nameof(PortalApplication),
                    resolution.ApplicationId) with
                {
                    Result = AuditResult.Failure,
                    Metadata = AuditMetadata.Create()
                        .Set("applicationKey", resolution.ApplicationKey)
                        .Set("reason", "invalidLaunchUrl"),
                },
                cancellationToken);

            Response.StatusCode = StatusCodes.Status403Forbidden;

            return View("LaunchDenied", new LaunchDeniedViewModel
            {
                ApplicationName = resolution.ApplicationName,
                Reason = AccessDenialReason.ApplicationDisabled,
            });
        }

        await _audit.RecordAndSaveAsync(
            AuditEntry.For(
                AuditActions.ApplicationLaunched,
                nameof(PortalApplication),
                resolution.ApplicationId) with
            {
                Metadata = AuditMetadata.Create().Set("applicationKey", resolution.ApplicationKey),
            },
            cancellationToken);

        // A deliberate off-site redirect, not an open redirect: the target comes from the
        // catalogue an administrator curates, never from the request.
        return Redirect(resolution.LaunchUrl!);
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
