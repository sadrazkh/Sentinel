using Microsoft.EntityFrameworkCore;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Catalog;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Notifications;
using Sentinel.Domain.Security;
using Sentinel.Domain.Subscriptions;

namespace Sentinel.Application.Abstractions;

/// <summary>
/// The persistence surface the application layer is allowed to touch.
/// <para>
/// Application services compose LINQ directly against these sets — there is no generic
/// repository wrapper, because EF Core's <see cref="DbSet{TEntity}"/> already is one and
/// hiding <c>Include</c>/projection behind it only costs query quality. Where a query gets
/// non-trivial it becomes a named query service instead.
/// </para>
/// </summary>
public interface ISentinelDbContext
{
    DbSet<ApplicationUser> Users { get; }

    DbSet<Membership> Memberships { get; }

    DbSet<PortalApplication> PortalApplications { get; }

    DbSet<UserEntitlement> UserEntitlements { get; }

    DbSet<AuditLog> AuditLogs { get; }

    DbSet<LoginAttempt> LoginAttempts { get; }

    DbSet<UserSession> UserSessions { get; }

    DbSet<Notification> Notifications { get; }

    DbSet<TelegramLinkToken> TelegramLinkTokens { get; }

    DbSet<SubscriptionSource> SubscriptionSources { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
