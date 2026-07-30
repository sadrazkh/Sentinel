using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Users;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Users;

/// <summary>
/// Reads role names straight from the join tables.
/// <para>
/// Not through <c>UserManager.GetRolesAsync</c>: that loads the whole user first, and this is
/// called on a read path that already has the id. One join returns exactly what is needed.
/// </para>
/// </summary>
public sealed class MemberRoleQuery : IMemberRoleQuery
{
    private readonly SentinelDbContext _db;

    public MemberRoleQuery(SentinelDbContext db) => _db = db;

    public async Task<IReadOnlySet<string>> GetRoleNamesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var names = await _db.UserRoles
            .AsNoTracking()
            .Where(link => link.UserId == userId)
            .Join(_db.Roles, link => link.RoleId, role => role.Id, (_, role) => role.Name)
            .Where(name => name != null)
            .ToListAsync(cancellationToken);

        // Case-insensitive: role names come from Identity's normalised store on one side and an
        // operator's typing on the other, and a case difference is not a different role.
        return names.Select(name => name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
