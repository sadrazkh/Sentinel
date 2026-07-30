using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sentinel.Vpn.Provisioning;

public sealed class ProvisioningOptions
{
    public const string SectionName = "Vpn:Provisioning";

    /// <summary>
    /// Whether the worker runs at all. Off would leave every service stuck in Pending, so this exists
    /// for a deployment that deliberately runs the workers in a separate process.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often to look for work. Short, because a member waiting for their first configuration is
    /// waiting on this tick.
    /// </summary>
    [Range(5, 3600)]
    public int JobIntervalSeconds { get; set; } = 20;

    /// <summary>Jobs per sweep. Each is at least one panel round-trip, so this bounds the burst.</summary>
    [Range(1, 200)]
    public int JobBatchSize { get; set; } = 10;

    /// <summary>
    /// How often to pull traffic counters. Minutes, not seconds: quotas are measured in gigabytes and
    /// polling every service every few seconds would be a lot of panel load for no extra accuracy.
    /// </summary>
    [Range(1, 1440)]
    public int UsageIntervalMinutes { get; set; } = 15;

    [Range(1, 500)]
    public int UsageBatchSize { get; set; } = 50;

    /// <summary>
    /// How often to resolve services parked with an unknown outcome. Frequent, because each one is a
    /// customer whose service is in limbo.
    /// </summary>
    [Range(1, 1440)]
    public int ReconcileIntervalMinutes { get; set; } = 5;

    [Range(1, 200)]
    public int ReconcileBatchSize { get; set; } = 20;

    /// <summary>Delay before the first sweep, so start-up is not competing for the connection pool.</summary>
    [Range(0, 600)]
    public int StartupDelaySeconds { get; set; } = 25;
}

/// <summary>
/// Drives provisioning, reconciliation and usage syncing on their own cadences.
/// <para>
/// Three separate timers rather than one, because the right frequency differs by an order of
/// magnitude: a queued job is a customer waiting, whereas a traffic counter measured in gigabytes
/// does not change meaningfully inside a minute.
/// </para>
/// <para>
/// Runs in every replica. Safe by construction: claiming a job is a guarded write, so exactly one
/// replica runs each, and the other simply finds nothing to do.
/// </para>
/// </summary>
public sealed class ProvisioningBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<ProvisioningOptions> _options;
    private readonly ILogger<ProvisioningBackgroundService> _logger;

    public ProvisioningBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<ProvisioningOptions> options,
        ILogger<ProvisioningBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;

        if (!options.Enabled)
        {
            _logger.LogWarning(
                "VPN provisioning workers are disabled. Queued jobs will not run in this process.");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(options.StartupDelaySeconds), stoppingToken);

        // Three independent loops. A slow usage sweep must not delay a queued provisioning job.
        var jobs = RunLoopAsync(
            TimeSpan.FromSeconds(options.JobIntervalSeconds),
            "provisioning",
            (executor, _, _, token) => executor.RunPendingAsync(options.JobBatchSize, token),
            stoppingToken);

        var reconcile = RunLoopAsync(
            TimeSpan.FromMinutes(options.ReconcileIntervalMinutes),
            "reconciliation",
            (_, reconciler, _, token) => reconciler.ReconcileAsync(options.ReconcileBatchSize, token),
            stoppingToken);

        var usage = RunLoopAsync(
            TimeSpan.FromMinutes(options.UsageIntervalMinutes),
            "usage sync",
            (_, reconciler, _, token) => reconciler.SyncUsageAsync(options.UsageBatchSize, token),
            stoppingToken);

        await Task.WhenAll(jobs, reconcile, usage);
    }

    private async Task RunLoopAsync(
        TimeSpan interval,
        string name,
        Func<IProvisioningExecutor, IReconciliationService, IServiceProvider, CancellationToken, Task<int>> work,
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();

                var executor = scope.ServiceProvider.GetRequiredService<IProvisioningExecutor>();
                var reconciler = scope.ServiceProvider.GetRequiredService<IReconciliationService>();

                var handled = await work(executor, reconciler, scope.ServiceProvider, stoppingToken);

                if (handled > 0)
                {
                    _logger.LogInformation("VPN {Loop} sweep handled {Count} item(s).", name, handled);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad sweep must not end the loop: the next may well succeed, and a dead worker
                // leaves every subsequent customer's service stuck.
                _logger.LogError(ex, "The VPN {Loop} sweep failed.", name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
