using Sentinel.Application.Common;
using Sentinel.Domain.Auditing;

namespace Sentinel.Application.Auditing;

public sealed record AuditLogListItem(
    Guid Id,
    Guid? ActorUserId,
    string? ActorUserName,
    string Action,
    string EntityType,
    string? EntityId,
    DateTimeOffset OccurredAt,
    string? IpAddress,
    string? UserAgent,
    AuditResult Result,
    string? CorrelationId,
    string? MetadataJson);

public sealed record AuditLogFilter(
    string? Action = null,
    string? EntityType = null,
    Guid? ActorUserId = null,
    string? EntityId = null,
    AuditResult? Result = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = PagingDefaults.DefaultPageSize);

public interface IAuditLogQuery
{
    /// <summary>
    /// One page of audit entries, newest first. Always paged in the database — the audit table
    /// is the fastest-growing one in the system and must never be loaded whole.
    /// </summary>
    Task<PagedResult<AuditLogListItem>> SearchAsync(
        AuditLogFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>The distinct action names present, so the filter can offer a real list.</summary>
    Task<IReadOnlyList<string>> GetKnownActionsAsync(CancellationToken cancellationToken = default);
}
