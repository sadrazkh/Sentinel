using Sentinel.Infrastructure.Persistence;
using Sentinel.Infrastructure.Seeding;
using Sentinel.Web.Security;

namespace Sentinel.Web.Infrastructure;

/// <summary>
/// Fails the boot when a setting that is fine locally would be dangerous in production.
/// A misconfiguration that refuses to start is far cheaper than one that runs quietly for
/// a month.
/// </summary>
public static class StartupGuards
{
    public static void EnsureProductionSafety(
        IHostEnvironment environment,
        DatabaseOptions database,
        SentinelSecurityOptions security,
        SeedOptions seed,
        ILogger logger)
    {
        if (environment.IsProduction())
        {
            if (database.EnableSensitiveDataLogging)
            {
                throw new InvalidOperationException(
                    "Database:EnableSensitiveDataLogging must be false in Production: it writes " +
                    "query parameter values, which routinely contain personal data, into the logs.");
            }

            if (!security.RequireHttps)
            {
                throw new InvalidOperationException(
                    "Security:RequireHttps must be true in Production. Without it the " +
                    "authentication cookie is sent over plain HTTP.");
            }

            if (database.Provider == DatabaseProvider.Sqlite)
            {
                throw new InvalidOperationException(
                    "Database:Provider Sqlite is for local development and tests only. " +
                    "Use PostgreSql or SqlServer in Production.");
            }

            if (seed.IncludeSampleApplications)
            {
                throw new InvalidOperationException(
                    "Seed:IncludeSampleApplications must be false in Production; it inserts " +
                    "placeholder catalogue rows.");
            }
        }

        if (seed.SuperAdmin.Enabled)
        {
            logger.LogWarning(
                "Seed:SuperAdmin:Enabled is on. It is a no-op once a SuperAdmin exists, but turn " +
                "it off and clear Seed__SuperAdmin__Password after the first successful boot.");
        }

        if (database.MigrateOnStartup && environment.IsProduction())
        {
            logger.LogWarning(
                "Database:MigrateOnStartup is on in Production. With more than one replica, run " +
                "migrations as a separate deployment step instead so two instances cannot race.");
        }
    }
}
