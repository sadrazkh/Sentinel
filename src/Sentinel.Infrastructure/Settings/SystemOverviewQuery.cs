using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Options;
using Sentinel.Application.Settings;
using Sentinel.Domain.Products;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;

namespace Sentinel.Infrastructure.Settings;

public sealed class SystemOverviewQuery : ISystemOverviewQuery
{
    private readonly ISentinelDbContext _db;
    private readonly MembershipOptions _membershipOptions;
    private readonly TimeProvider _timeProvider;

    public SystemOverviewQuery(
        ISentinelDbContext db,
        IOptions<MembershipOptions> membershipOptions,
        TimeProvider timeProvider)
    {
        _db = db;
        _membershipOptions = membershipOptions.Value;
        _timeProvider = timeProvider;
    }

    public async Task<SystemCounters> GetCountersAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var renewalHorizon = now.AddDays(_membershipOptions.RenewalWarningDays);
        var last24Hours = now.AddHours(-24);

        // Aggregates only — no rows are materialised. Each of these is a COUNT the database
        // answers from an index; loading the tables to count them in memory is exactly what a
        // dashboard must not do.
        var totalUsers = await _db.Users.CountAsync(cancellationToken);
        var activeUsers = await _db.Users.CountAsync(u => u.Status == UserAccountStatus.Active, cancellationToken);
        var suspendedUsers = await _db.Users.CountAsync(u => u.Status == UserAccountStatus.Suspended, cancellationToken);
        var disabledUsers = await _db.Users.CountAsync(u => u.Status == UserAccountStatus.Disabled, cancellationToken);

        var activeMemberships = await _db.Memberships.CountAsync(
            m => m.AdminState == MembershipAdminState.Active
                 && m.StartsAt <= now
                 && (m.EndsAt == null || m.EndsAt >= now),
            cancellationToken);

        var expiringSoon = await _db.Memberships.CountAsync(
            m => m.AdminState == MembershipAdminState.Active
                 && m.EndsAt != null
                 && m.EndsAt >= now
                 && m.EndsAt <= renewalHorizon,
            cancellationToken);

        var totalApplications = await _db.Products.CountAsync(cancellationToken);
        var publishedApplications = await _db.Products.CountAsync(
            a => a.ReleaseStatus == ProductReleaseStatus.Stable && a.IsEnabled, cancellationToken);

        var activeEntitlements = await _db.ProductEntitlements.CountAsync(
            e => e.RevokedAt == null
                 && e.IsEnabled
                 && e.StartsAt <= now
                 && (e.ExpiresAt == null || e.ExpiresAt > now),
            cancellationToken);

        var activeSessions = await _db.UserSessions.CountAsync(
            s => s.RevokedAt == null && s.ExpiresAt > now, cancellationToken);

        var failedSignIns = await _db.LoginAttempts.CountAsync(
            a => !a.Succeeded && a.OccurredAt >= last24Hours, cancellationToken);

        var auditEntries = await _db.AuditLogs.LongCountAsync(cancellationToken);

        return new SystemCounters(
            totalUsers,
            activeUsers,
            suspendedUsers,
            disabledUsers,
            activeMemberships,
            expiringSoon,
            totalApplications,
            publishedApplications,
            activeEntitlements,
            activeSessions,
            failedSignIns,
            auditEntries);
    }
}
