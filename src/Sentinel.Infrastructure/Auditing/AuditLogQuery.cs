using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Common;

namespace Sentinel.Infrastructure.Auditing;

public sealed class AuditLogQuery : IAuditLogQuery
{
    private readonly ISentinelDbContext _db;

    public AuditLogQuery(ISentinelDbContext db) => _db = db;

    public async Task<PagedResult<AuditLogListItem>> SearchAsync(
        AuditLogFilter filter,
        CancellationToken cancellationToken = default)
    {
        var page = PagingDefaults.NormalizePage(filter.Page);
        var pageSize = PagingDefaults.NormalizePageSize(filter.PageSize);

        var query = _db.AuditLogs.AsNoTracking();

        // Every predicate is a parameterised LINQ comparison; nothing here is composed as SQL
        // text, so an operator's filter input cannot reach the query as anything but a value.
        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            var action = filter.Action.Trim();
            query = query.Where(a => a.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
        {
            var entityType = filter.EntityType.Trim();
            query = query.Where(a => a.EntityType == entityType);
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityId))
        {
            var entityId = filter.EntityId.Trim();
            query = query.Where(a => a.EntityId == entityId);
        }

        if (filter.ActorUserId is { } actorUserId)
        {
            query = query.Where(a => a.ActorUserId == actorUserId);
        }

        if (filter.Result is { } result)
        {
            query = query.Where(a => a.Result == result);
        }

        if (filter.From is { } from)
        {
            query = query.Where(a => a.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(a => a.OccurredAt <= to);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(a => a.OccurredAt)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuditLogListItem(
                a.Id,
                a.ActorUserId,
                a.ActorUserName,
                a.Action,
                a.EntityType,
                a.EntityId,
                a.OccurredAt,
                a.IpAddress,
                a.UserAgent,
                a.Result,
                a.CorrelationId,
                a.MetadataJson))
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditLogListItem>(items, page, pageSize, totalCount);
    }

    public async Task<IReadOnlyList<string>> GetKnownActionsAsync(
        CancellationToken cancellationToken = default) =>
        await _db.AuditLogs
            .AsNoTracking()
            .Select(a => a.Action)
            .Distinct()
            .OrderBy(action => action)
            .ToListAsync(cancellationToken);
}
