using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Catalog;
using Sentinel.Domain.Common;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Notifications;
using Sentinel.Domain.Security;
using Sentinel.Domain.Subscriptions;
using Sentinel.Infrastructure.Persistence.Converters;

namespace Sentinel.Infrastructure.Persistence;

public class SentinelDbContext
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>, ISentinelDbContext
{
    private readonly TimeProvider _timeProvider;

    public SentinelDbContext(DbContextOptions<SentinelDbContext> options, TimeProvider timeProvider)
        : base(options)
    {
        _timeProvider = timeProvider;
    }

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<PortalApplication> PortalApplications => Set<PortalApplication>();

    public DbSet<UserEntitlement> UserEntitlements => Set<UserEntitlement>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<TelegramLinkToken> TelegramLinkTokens => Set<TelegramLinkToken>();

    public DbSet<SubscriptionSource> SubscriptionSources => Set<SubscriptionSource>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(SentinelDbContext).Assembly);

        if (Database.IsSqlServer())
        {
            // SQL Server permits only a single NULL in a unique index, which would mean just
            // one account could exist without a phone number. Everywhere else NULLs are
            // distinct and no filter is needed.
            builder.Entity<ApplicationUser>()
                .HasIndex(u => u.NormalizedPhoneNumber)
                .IsUnique()
                .HasFilter("[NormalizedPhoneNumber] IS NOT NULL");

            builder.Entity<ApplicationUser>()
                .HasIndex(u => u.TelegramUserId)
                .IsUnique()
                .HasFilter("[TelegramUserId] IS NOT NULL");
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // SQLite cannot translate comparisons or ordering on DateTimeOffset, and this model is
        // full of them ("is this session still valid?", "has this membership expired?").
        // Converting to a UTC DateTime for that provider only keeps the domain type expressive
        // while leaving PostgreSQL and SQL Server on their native timestamp types.
        if (Database.IsSqlite())
        {
            configurationBuilder.Properties<DateTimeOffset>()
                .HaveConversion<DateTimeOffsetToUtcDateTimeConverter>();
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyTimestamps();
        RotateConcurrencyTokens();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        ApplyTimestamps();
        RotateConcurrencyTokens();
        return base.SaveChanges();
    }

    private void ApplyTimestamps()
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var entry in ChangeTracker.Entries<ITimestamped>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreatedAt == default)
                    {
                        entry.Entity.CreatedAt = now;
                    }

                    entry.Entity.UpdatedAt = now;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    // CreatedAt is immutable once written.
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    break;
            }
        }
    }

    /// <summary>
    /// Issues a fresh token on every write. EF keeps the *original* value for the UPDATE's
    /// WHERE clause, so a second writer that loaded the row earlier fails with a
    /// <see cref="DbUpdateConcurrencyException"/> instead of silently overwriting.
    /// </summary>
    private void RotateConcurrencyTokens()
    {
        foreach (EntityEntry<IConcurrencyAware> entry in ChangeTracker.Entries<IConcurrencyAware>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.ConcurrencyToken = Guid.NewGuid();
            }
        }
    }
}
