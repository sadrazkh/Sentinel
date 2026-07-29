using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Sentinel.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real application — the real pipeline, the real Identity configuration, the real
/// authorization policies — against a private SQLite database.
/// <para>
/// SQLite keeps the suite fast and free of any Docker or server dependency, and exercising a
/// second provider is itself proof that nothing in the model has quietly become
/// PostgreSQL-specific. Nothing about authentication or authorization is stubbed: these tests
/// would be worthless if the thing under test were replaced by a test double.
/// </para>
/// </summary>
public class SentinelWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminUserName = "test-admin";
    public const string AdminEmail = "test-admin@sentinel.invalid";

    /// <summary>Obviously synthetic, used only by this suite, and never a real credential.</summary>
    public const string AdminPassword = "Integration-Test-Only-987654";

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SentinelWebApplicationFactory()
    {
        // A shared in-memory database lives only while at least one connection to it is open,
        // so this one is held for the lifetime of the factory.
        _connectionString = $"Data Source=sentinel-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development (no developer exception page) and not Production (whose start-up
        // guards reject SQLite outright).
        builder.UseEnvironment("Testing");

        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:ConnectionString", _connectionString);
        builder.UseSetting("ConnectionStrings:Sentinel", _connectionString);

        // The test host speaks plain HTTP to a loopback address, so HTTPS redirection and
        // Secure/__Host- cookies would stop every request from ever completing.
        builder.UseSetting("Security:RequireHttps", "false");

        // High enough that ordinary tests never trip it; the rate-limit test lowers it.
        builder.UseSetting("Security:LoginRateLimit:PermitLimit", "1000");

        builder.UseSetting("Seed:SuperAdmin:Enabled", "true");
        builder.UseSetting("Seed:SuperAdmin:UserName", AdminUserName);
        builder.UseSetting("Seed:SuperAdmin:Email", AdminEmail);
        builder.UseSetting("Seed:SuperAdmin:DisplayName", "Test Administrator");
        builder.UseSetting("Seed:SuperAdmin:Password", AdminPassword);
        builder.UseSetting("Seed:IncludeSampleApplications", "false");

        ConfigureTestSettings(builder);
    }

    /// <summary>Hook for a derived factory that needs a different configuration.</summary>
    protected virtual void ConfigureTestSettings(IWebHostBuilder builder)
    {
    }

    /// <summary>A client that does not chase redirects, so the tests can assert on them.</summary>
    public HttpClient CreateNonRedirectingClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public async Task WithScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _keepAliveConnection.Dispose();
        }
    }
}
