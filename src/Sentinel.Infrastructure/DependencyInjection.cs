using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Accounts;
using Sentinel.Application.Auditing;
using Sentinel.Application.Security;
using Sentinel.Infrastructure.Accounts;
using Sentinel.Infrastructure.Auditing;
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
