using Microsoft.EntityFrameworkCore;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Billing;
using Sentinel.Domain.Products;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Notifications;
using Sentinel.Domain.Security;
using Sentinel.Domain.Settings;
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

    DbSet<Product> Products { get; }

    DbSet<ProductCategory> ProductCategories { get; }

    DbSet<ProductSection> ProductSections { get; }

    DbSet<ProductDownload> ProductDownloads { get; }

    DbSet<DocumentationCategory> DocumentationCategories { get; }

    DbSet<DocumentationArticle> DocumentationArticles { get; }

    DbSet<DocumentationStep> DocumentationSteps { get; }

    DbSet<ProductEntitlement> ProductEntitlements { get; }

    DbSet<AuditLog> AuditLogs { get; }

    DbSet<LoginAttempt> LoginAttempts { get; }

    DbSet<UserSession> UserSessions { get; }

    DbSet<Notification> Notifications { get; }

    DbSet<TelegramLinkToken> TelegramLinkTokens { get; }

    DbSet<SubscriptionSource> SubscriptionSources { get; }

    DbSet<FeatureOverride> FeatureOverrides { get; }

    DbSet<Wallet> Wallets { get; }

    DbSet<WalletTransaction> WalletTransactions { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-reads a tracked entity, discarding local changes.
    /// <para>
    /// Needed after a concurrency conflict: the tracked copy still carries the original token, so
    /// retrying with it would resubmit the same losing UPDATE for ever.
    /// </para>
    /// </summary>
    Task ReloadAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default)
        where TEntity : class;

    /// <summary>
    /// Stops tracking an entity.
    /// <para>
    /// Used when a failed save has to be retried with a fresh decision: the row that was going to be
    /// inserted is no longer the row that should be, and leaving it attached would insert the stale
    /// one on the next save.
    /// </para>
    /// </summary>
    void Detach<TEntity>(TEntity entity) where TEntity : class;
}
