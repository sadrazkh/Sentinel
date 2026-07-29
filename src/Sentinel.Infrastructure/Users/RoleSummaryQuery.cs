using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Users;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Users;

public sealed class RoleSummaryQuery : IRoleSummaryQuery
{
    private readonly SentinelDbContext _db;

    // Takes the concrete context because Identity's join tables are not part of the
    // application-facing ISentinelDbContext surface — nothing outside Infrastructure should
    // be composing queries against them.
    public RoleSummaryQuery(SentinelDbContext db) => _db = db;

    public async Task<IReadOnlyList<RoleSummary>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await _db.Roles
            .AsNoTracking()
            .OrderBy(role => role.Name)
            .Select(role => new RoleSummary(
                role.Name!,
                role.Description,
                // A correlated count, so the whole list is one round trip rather than one
                // membership query per role.
                _db.UserRoles.Count(userRole => userRole.RoleId == role.Id)))
            .ToListAsync(cancellationToken);
}
