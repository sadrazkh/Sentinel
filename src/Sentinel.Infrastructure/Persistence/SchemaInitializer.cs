using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sentinel.Infrastructure.Persistence;

/// <summary>
/// Brings the database schema up to date, choosing the correct mechanism per provider.
/// <para>
/// The migration set committed in this repository is generated for PostgreSQL, so applying
/// it against another engine would run PostgreSQL-specific DDL. Rather than fail with an
/// obscure SQL error, each provider gets an explicit path and SQL Server gets a message that
/// says exactly what to do.
/// </para>
/// </summary>
public sealed class SchemaInitializer
{
    private readonly SentinelDbContext _db;
    private readonly DatabaseOptions _options;
    private readonly ILogger<SchemaInitializer> _logger;

    public SchemaInitializer(
        SentinelDbContext db,
        IOptions<DatabaseOptions> options,
        ILogger<SchemaInitializer> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        switch (_options.Provider)
        {
            case DatabaseProvider.Sqlite:
                // Dev and test provider: the schema is created straight from the model, so a
                // second migration set never has to be kept in step with the PostgreSQL one.
                await _db.Database.EnsureCreatedAsync(cancellationToken);
                _logger.LogInformation("SQLite schema ensured from the current model.");
                return;

            case DatabaseProvider.PostgreSql:
                if (!_options.MigrateOnStartup)
                {
                    _logger.LogInformation(
                        "Database:MigrateOnStartup is off. Apply migrations as a deployment step.");
                    return;
                }

                var pending = (await _db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                if (pending.Count == 0)
                {
                    _logger.LogInformation("Database schema is up to date.");
                    return;
                }

                _logger.LogInformation("Applying {Count} pending migration(s): {Migrations}",
                    pending.Count, string.Join(", ", pending));
                await _db.Database.MigrateAsync(cancellationToken);
                return;

            case DatabaseProvider.SqlServer:
                if (_options.MigrateOnStartup)
                {
                    throw new InvalidOperationException(
                        "Database:MigrateOnStartup is not supported for SQL Server in this repository: " +
                        "the committed migration set targets PostgreSQL. Generate a SQL Server set " +
                        "(see the database section of the README) and apply it as a deployment step, " +
                        "then set Database:MigrateOnStartup to false.");
                }

                _logger.LogInformation(
                    "SQL Server provider selected. Schema management is left to your own migration set.");
                return;

            default:
                throw new InvalidOperationException($"Unsupported database provider '{_options.Provider}'.");
        }
    }
}
