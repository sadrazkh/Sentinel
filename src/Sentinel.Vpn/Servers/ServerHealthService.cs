using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Panel;
using Sentinel.Vpn.Persistence;

namespace Sentinel.Vpn.Servers;

/// <summary>
/// Re-checks every panel on a timer.
/// <para>
/// Without this, a panel that died stays <see cref="VpnServerStatus.Active"/> and selection keeps
/// placing new services on it — each one failing at provisioning time, which is the worst moment
/// to discover it. The sweep moves a dead panel out of selection before a customer meets it.
/// </para>
/// <para>
/// Runs in every replica. That is safe rather than wasteful: the write is idempotent and the row's
/// concurrency token means the loser of a simultaneous check simply does not write. Coordinating
/// it would need a leader election, which is a lot of machinery for a health poll.
/// </para>
/// </summary>
public sealed class ServerHealthService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<ThreeXUiOptions> _options;
    private readonly ILogger<ServerHealthService> _logger;

    public ServerHealthService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<ThreeXUiOptions> options,
        ILogger<ServerHealthService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.CurrentValue.HealthCheckIntervalMinutes;

        if (interval <= 0)
        {
            _logger.LogInformation("VPN server health checks are disabled.");
            return;
        }

        // A short delay so start-up is not competing with the first sweep for the connection pool.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(interval));

        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let one bad sweep end the loop: the next one may well succeed, and a dead
                // health checker is worse than a failed check.
                _logger.LogError(ex, "The VPN server health sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<IVpnDbContext>();
        var admin = scope.ServiceProvider.GetRequiredService<IVpnServerAdminService>();

        // Disabled servers are skipped: an operator withdrew them deliberately, and probing one
        // would keep contacting a panel they may have decommissioned.
        var candidates = await db.VpnServers
            .AsNoTracking()
            .Where(server => server.Status != VpnServerStatus.Disabled)
            .Select(server => server.Id)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return;
        }

        var recovered = 0;
        var lost = 0;

        foreach (var serverId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Sequential on purpose. A handful of panels is the normal case, and firing every
            // probe at once would mean a burst of connections on every tick for no real gain.
            var result = await admin.ProbeAsync(serverId, cancellationToken);

            if (!result.Succeeded)
            {
                lost++;
                continue;
            }

            if (result.Value!.Health == VpnServerHealth.Healthy)
            {
                recovered++;
            }
            else
            {
                lost++;
            }
        }

        _logger.LogInformation(
            "VPN health sweep checked {Count} server(s): {Healthy} healthy, {Unhealthy} not.",
            candidates.Count,
            recovered,
            lost);
    }
}
