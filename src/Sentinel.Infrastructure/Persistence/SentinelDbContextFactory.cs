using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sentinel.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>. Migrations in this repository target PostgreSQL, so the
/// design-time context always uses the Npgsql provider; the connection string is read from
/// <c>SENTINEL_MIGRATIONS_CONNECTION</c> and never has to point at a live database for
/// <c>migrations add</c>.
/// </summary>
public sealed class SentinelDbContextFactory : IDesignTimeDbContextFactory<SentinelDbContext>
{
    private const string DesignTimeFallbackConnection =
        "Host=localhost;Port=5432;Database=sentinel;Username=sentinel;Password=design-time-only";

    public SentinelDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("SENTINEL_MIGRATIONS_CONNECTION")
            ?? DesignTimeFallbackConnection;

        var options = new DbContextOptionsBuilder<SentinelDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory"))
            .Options;

        return new SentinelDbContext(options, TimeProvider.System);
    }
}
