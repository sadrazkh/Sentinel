using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Accounts;
using Sentinel.Application.Common;

namespace Sentinel.Infrastructure.Accounts;

public sealed class ActivityQuery : IActivityQuery
{
    private readonly ISentinelDbContext _db;

    public ActivityQuery(ISentinelDbContext db) => _db = db;

    public async Task<PagedResult<ActivityEntry>> GetSignInHistoryAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = PagingDefaults.NormalizePage(page);
        pageSize = PagingDefaults.NormalizePageSize(pageSize);

        // Scoped to one user in SQL, not filtered afterwards. The index on
        // (UserId, OccurredAt) makes both the count and the page a range scan.
        var query = _db.LoginAttempts
            .AsNoTracking()
            .Where(a => a.UserId == userId);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(a => a.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new ActivityEntry(
                a.OccurredAt,
                a.Succeeded,
                a.FailureReason,
                a.IpAddress,
                a.UserAgent))
            .ToListAsync(cancellationToken);

        return new PagedResult<ActivityEntry>(items, page, pageSize, totalCount);
    }
}
