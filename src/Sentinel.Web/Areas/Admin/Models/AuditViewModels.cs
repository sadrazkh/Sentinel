using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Application.Auditing;
using Sentinel.Application.Common;
using Sentinel.Domain.Auditing;

namespace Sentinel.Web.Areas.Admin.Models;

public sealed class AuditFilterViewModel
{
    /// <summary>
    /// Bound from <c>auditAction</c>, not <c>action</c>.
    /// <para>
    /// MVC reserves <c>action</c> as a route value, and model binding considers route values
    /// alongside the query string — so a property bound from <c>action</c> silently receives
    /// the controller action's own name ("Index") instead of the filter the operator chose.
    /// The filter then matches nothing and looks like an empty audit log.
    /// </para>
    /// </summary>
    [FromQuery(Name = "auditAction")]
    [StringLength(AuditLog.ActionMaxLength)]
    public string? Action { get; set; }

    [StringLength(AuditLog.EntityTypeMaxLength)]
    public string? EntityType { get; set; }

    [StringLength(AuditLog.EntityIdMaxLength)]
    public string? EntityId { get; set; }

    public Guid? ActorUserId { get; set; }

    public AuditResult? Result { get; set; }

    [DataType(DataType.Date)]
    public DateTime? From { get; set; }

    [DataType(DataType.Date)]
    public DateTime? To { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = PagingDefaults.DefaultPageSize;

    public bool HasAnyFilter =>
        !string.IsNullOrWhiteSpace(Action)
        || !string.IsNullOrWhiteSpace(EntityType)
        || !string.IsNullOrWhiteSpace(EntityId)
        || ActorUserId is not null
        || Result is not null
        || From is not null
        || To is not null;

    /// <summary>Route values for the pager, so paging keeps the current filter.</summary>
    public Dictionary<string, string?> ToRouteValues(int page) => new()
    {
        ["auditAction"] = Action,
        ["EntityType"] = EntityType,
        ["EntityId"] = EntityId,
        ["ActorUserId"] = ActorUserId?.ToString(),
        ["Result"] = Result?.ToString(),
        ["From"] = From?.ToString("yyyy-MM-dd"),
        ["To"] = To?.ToString("yyyy-MM-dd"),
        ["PageSize"] = PageSize.ToString(),
        ["Page"] = page.ToString(),
    };
}

public sealed class AuditIndexViewModel
{
    public required AuditFilterViewModel Filter { get; init; }

    public required PagedResult<AuditLogListItem> Entries { get; init; }

    public required IReadOnlyList<string> KnownActions { get; init; }

    public required IReadOnlyList<AuditResult> KnownResults { get; init; }

    public required string TimeZoneId { get; init; }
}

public sealed class SystemSettingsViewModel
{
    public required Sentinel.Application.Settings.SystemCounters Counters { get; init; }

    public required IReadOnlyList<Sentinel.Application.Settings.SettingRow> Settings { get; init; }

    public required IReadOnlyList<RoleSummaryRow> Roles { get; init; }

    public required string EnvironmentName { get; init; }

    public required string Version { get; init; }
}

public sealed record RoleSummaryRow(string Name, string? Description, int MemberCount);
