using Sentinel.Infrastructure.Persistence;
using Sentinel.Infrastructure.Seeding;

namespace Sentinel.Web.Infrastructure;

public static class DatabaseInitializationExtensions
{
    /// <summary>
    /// Brings the schema up to date and runs idempotent seeding before the first request is
    /// served. Failures are logged and rethrown: an instance whose schema is wrong should
    /// refuse to start rather than serve half-working pages.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Sentinel.DatabaseInitialization");

        try
        {
            await scope.ServiceProvider.GetRequiredService<SchemaInitializer>().InitializeAsync();
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database initialisation failed; the application will not start.");
            throw;
        }
    }
}
