using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Auditing;
using Sentinel.Application.Authorization;
using Sentinel.Application.Common;
using Sentinel.Domain.Auditing;
using Sentinel.Web.Areas.Admin.Models;
using Sentinel.Web.Infrastructure;

namespace Sentinel.Web.Areas.Admin.Controllers;

/// <summary>
/// The audit trail viewer. Read-only by design — there is no endpoint that edits or deletes an
/// entry, because an audit log that can be rewritten from the application that writes it is
/// not evidence of anything.
/// </summary>
[Area("Admin")]
[Authorize(Policy = PolicyNames.BackOfficeRead)]
public sealed class AuditController : Controller
{
    private readonly IAuditLogQuery _auditLog;

    public AuditController(IAuditLogQuery auditLog) => _auditLog = auditLog;

    [HttpGet]
    public async Task<IActionResult> Index(
        AuditFilterViewModel filter,
        CancellationToken cancellationToken)
    {
        var entries = await _auditLog.SearchAsync(
            new AuditLogFilter(
                Action: filter.Action,
                EntityType: filter.EntityType,
                ActorUserId: filter.ActorUserId,
                EntityId: filter.EntityId,
                Result: filter.Result,
                From: filter.From is { } from
                    ? new DateTimeOffset(from.Date, TimeSpan.Zero)
                    : null,
                // Inclusive of the whole chosen day, which is what an operator means by "to".
                To: filter.To is { } to
                    ? new DateTimeOffset(to.Date, TimeSpan.Zero).AddDays(1).AddTicks(-1)
                    : null,
                Page: filter.Page,
                PageSize: filter.PageSize),
            cancellationToken);

        var actions = await _auditLog.GetKnownActionsAsync(cancellationToken);

        return View(new AuditIndexViewModel
        {
            Filter = filter,
            Entries = entries,
            KnownActions = actions,
            KnownResults = Enum.GetValues<AuditResult>(),
            TimeZoneId = UserTime.DefaultTimeZoneId,
        });
    }
}
