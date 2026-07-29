using System.ComponentModel.DataAnnotations;

namespace Sentinel.Infrastructure.Persistence;

public enum DatabaseProvider
{
    /// <summary>Production default. Migrations in this repository are generated for PostgreSQL.</summary>
    PostgreSql = 1,

    /// <summary>
    /// Zero-setup local development and automated tests. The schema is created from the model
    /// rather than from migrations, so no second migration set has to be kept in sync.
    /// </summary>
    Sqlite = 2,

    /// <summary>
    /// Supported at runtime. Requires its own migration set — see the database section of the
    /// README for the single command that generates it.
    /// </summary>
    SqlServer = 3,
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.PostgreSql;

    /// <summary>
    /// Never committed. Supplied through <c>ConnectionStrings:Sentinel</c>, an environment
    /// variable or a secret store.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Applies pending migrations at startup. Convenient for a single-instance deployment;
    /// turn it off and run migrations as a deploy step once you run more than one replica.
    /// </summary>
    public bool MigrateOnStartup { get; set; }

    [Range(0, 300)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Emits parameter values into logs. Development only — parameter values routinely contain
    /// personal data, so this stays off unless explicitly enabled.
    /// </summary>
    public bool EnableSensitiveDataLogging { get; set; }
}
