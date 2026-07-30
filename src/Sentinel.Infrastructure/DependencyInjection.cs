using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Access;
using Sentinel.Application.Accounts;
using Sentinel.Application.Auditing;
using Sentinel.Application.Catalog;
using Sentinel.Application.Entitlements;
using Sentinel.Application.Features;
using Sentinel.Application.Media;
using Sentinel.Application.Memberships;
using Sentinel.Application.Notifications;
using Sentinel.Application.Products;
using Sentinel.Application.Security;
using Sentinel.Application.Settings;
using Sentinel.Application.Subscriptions;
using Sentinel.Application.Users;
using Sentinel.Application.Billing;
using Sentinel.Infrastructure.Access;
using Sentinel.Infrastructure.Accounts;
using Sentinel.Infrastructure.Auditing;
using Sentinel.Infrastructure.Billing;
using Sentinel.Infrastructure.Catalog;
using Sentinel.Infrastructure.Entitlements;
using Sentinel.Infrastructure.Features;
using Sentinel.Infrastructure.Media;
using Sentinel.Infrastructure.Memberships;
using Sentinel.Infrastructure.Notifications;
using Sentinel.Infrastructure.Products;
using Sentinel.Infrastructure.Settings;
using Sentinel.Infrastructure.Subscriptions;
using Sentinel.Infrastructure.Users;
using Sentinel.Infrastructure.Persistence;
using Sentinel.Infrastructure.Security;
using Sentinel.Infrastructure.Seeding;

namespace Sentinel.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the DbContext against the configured provider.
    /// <para>
    /// The provider is the only place in the solution that knows which engine is in use:
    /// entity configurations avoid provider-specific column types, and application code talks
    /// to <see cref="ISentinelDbContext"/>, so switching engines is a configuration change.
    /// </para>
    /// </summary>
    public static IServiceCollection AddSentinelPersistence(
        this IServiceCollection services,
        DatabaseOptions databaseOptions)
    {
        ArgumentNullException.ThrowIfNull(databaseOptions);

        if (string.IsNullOrWhiteSpace(databaseOptions.ConnectionString))
        {
            throw new InvalidOperationException(
                "No database connection string configured. Set ConnectionStrings:Sentinel " +
                "(or Database:ConnectionString) through configuration, an environment variable " +
                "or a secret store.");
        }

        // A factory plus a scoped instance built from it: most code shares the request's
        // context, while services that must commit independently (audit, login attempts) can
        // take a short-lived one without disturbing the caller's unit of work.
        services.AddDbContextFactory<SentinelDbContext>(
            (_, builder) => ConfigureProvider(builder, databaseOptions),
            ServiceLifetime.Scoped);

        services.AddScoped<SentinelDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<SentinelDbContext>>().CreateDbContext());

        services.AddScoped<ISentinelDbContext>(sp => sp.GetRequiredService<SentinelDbContext>());

        services.AddScoped<SchemaInitializer>();
        services.AddScoped<DatabaseSeeder>();

        return services;
    }

    public static IServiceCollection AddSentinelInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IUserSessionService, UserSessionService>();
        services.AddScoped<ILoginAttemptService, LoginAttemptService>();
        services.AddScoped<IAccountOverviewQuery, AccountOverviewQuery>();

        // Stateless apart from its options, so a singleton is enough.
        services.AddSingleton<IMembershipStatusResolver, MembershipStatusResolver>();
        services.AddScoped<IAccessDecisionService, AccessDecisionService>();

        // The gate layers an operator's switches over configuration. Both are singletons: a
        // feature check sits on request paths and must answer from memory, never from a query.
        services.AddSingleton<IFeatureOverrideStore, FeatureOverrideStore>();
        services.AddSingleton<IFeatureGate, FeatureGate>();

        // Writing a switch is rare and needs a unit of work, so it is scoped and kept apart from
        // the gate — nothing on a hot path can then acquire a database dependency by accident.
        services.AddScoped<IFeatureAdminService, FeatureAdminService>();
        services.AddScoped<IProductLibraryService, ProductLibraryService>();
        services.AddScoped<IProductContentService, ProductContentService>();
        services.AddScoped<IProductContentAdminService, ProductContentAdminService>();
        services.AddScoped<IProductContentAdminQuery, ProductContentAdminQuery>();

        services.AddScoped<IUserAdminQuery, UserAdminQuery>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IMembershipAdminService, MembershipAdminService>();

        services.AddScoped<IApplicationAdminQuery, ApplicationAdminQuery>();
        services.AddScoped<IApplicationAdminService, ApplicationAdminService>();
        services.AddScoped<IEntitlementAdminQuery, EntitlementAdminQuery>();
        services.AddScoped<IEntitlementAdminService, EntitlementAdminService>();

        // The credit ledger. Registered unconditionally; the feature flag is checked inside it, so
        // a caller that slipped past a controller's gate is still refused where the money moves.
        services.AddScoped<IWalletService, WalletService>();

        // Stateless once its root path is resolved.
        services.AddSingleton<IApplicationIconStorage, LocalApplicationIconStorage>();

        services.AddScoped<IProfileService, ProfileService>();
        services.AddScoped<IActivityQuery, ActivityQuery>();
        services.AddScoped<IAuditLogQuery, AuditLogQuery>();
        services.AddScoped<ISystemOverviewQuery, SystemOverviewQuery>();
        services.AddScoped<IRoleSummaryQuery, RoleSummaryQuery>();
        services.AddScoped<IMemberRoleQuery, MemberRoleQuery>();

        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ITelegramLinkService, TelegramLinkService>();

        // One fetcher for the process: it owns a pooled HttpClient whose handler carries the
        // connect-time address validation, and creating one per request would discard the
        // connection pool along with it.
        services.AddSingleton<ISubscriptionFetcher, SubscriptionFetcher>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        services.AddScoped<ISubscriptionAdminService, SubscriptionAdminService>();
        services.AddMemoryCache();

        // Runs in every replica. Safe to do so: the stage marker it writes sits on a row with
        // an optimistic concurrency token, so a simultaneous sweep elsewhere loses the write
        // and sends nothing.
        services.AddHostedService<ExpiryNoticeService>();

        return services;
    }

    /// <summary>
    /// Registers the Telegram bot client and its two background services.
    /// <para>
    /// The client is registered as <c>null</c> when no token is configured, rather than the
    /// feature being absent from the container: that way the channel and the link service
    /// resolve normally and report "not configured" instead of the application failing to start
    /// because an optional integration was left unset.
    /// </para>
    /// </summary>
    public static IServiceCollection AddSentinelTelegram(
        this IServiceCollection services,
        TelegramOptions telegramOptions)
    {
        services.AddSingleton<ITelegramClientProvider, TelegramClientProvider>();
        services.AddSingleton<INotificationChannel, TelegramNotificationChannel>();

        if (telegramOptions.IsConfigured)
        {
            services.AddHostedService<NotificationDeliveryService>();

            if (telegramOptions.UsePolling)
            {
                services.AddHostedService<TelegramBotPollingService>();
            }
        }

        return services;
    }

    private static void ConfigureProvider(DbContextOptionsBuilder builder, DatabaseOptions options)
    {
        var timeout = options.CommandTimeoutSeconds;

        switch (options.Provider)
        {
            case DatabaseProvider.PostgreSql:
                builder.UseNpgsql(options.ConnectionString, npgsql =>
                {
                    npgsql.CommandTimeout(timeout);
                    npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
                });
                break;

            case DatabaseProvider.Sqlite:
                builder.UseSqlite(options.ConnectionString, sqlite => sqlite.CommandTimeout(timeout));
                break;

            case DatabaseProvider.SqlServer:
                builder.UseSqlServer(options.ConnectionString, sql =>
                {
                    sql.CommandTimeout(timeout);
                    sql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
                });
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider '{options.Provider}'.");
        }

        if (options.EnableSensitiveDataLogging)
        {
            // Parameter values routinely contain personal data, so this is opt-in and is
            // rejected outright in Production by SentinelOptionsValidation.
            builder.EnableSensitiveDataLogging();
            builder.EnableDetailedErrors();
        }
    }
}
