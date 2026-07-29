using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Accounts;
using Sentinel.Application.Security;

namespace Sentinel.Infrastructure.Accounts;

public sealed class AccountOverviewQuery : IAccountOverviewQuery
{
    private readonly ISentinelDbContext _db;
    private readonly TimeProvider _timeProvider;

    public AccountOverviewQuery(ISentinelDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<AccountOverview?> GetAsync(
        Guid userId,
        int recentLoginCount,
        CancellationToken cancellationToken = default)
    {
        recentLoginCount = Math.Clamp(recentLoginCount, 1, 50);
        var now = _timeProvider.GetUtcNow();

        // One round trip, projected into the DTO. AsNoTracking because nothing here is
        // written back, and the projection keeps unused columns off the wire.
        return await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new AccountOverview(
                u.Id,
                u.DisplayName,
                u.UserName!,
                u.Email,
                u.Status,
                u.SuspendedUntil,
                u.CreatedAt,
                u.LastLoginAt,
                u.PreferredCulture,
                u.TimeZoneId,
                u.Sessions.Count(s => s.RevokedAt == null && s.ExpiresAt > now),
                u.LoginAttempts
                    .OrderByDescending(a => a.OccurredAt)
                    .Take(recentLoginCount)
                    .Select(a => new LoginAttemptView(
                        a.OccurredAt,
                        a.Succeeded,
                        a.FailureReason,
                        a.IpAddress,
                        a.UserAgent))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
