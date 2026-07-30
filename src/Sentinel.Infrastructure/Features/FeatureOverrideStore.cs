using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Features;

namespace Sentinel.Infrastructure.Features;

/// <summary>
/// Holds the switch positions in memory so the gate can answer without a query.
/// <para>
/// A singleton with a snapshot, refreshed after every write and whenever the snapshot is older than
/// <see cref="StaleAfter"/>. The timer is what makes this work across replicas: a switch moved on
/// one instance reaches the others within that window, with no message bus and nothing to keep in
/// sync beyond a table.
/// </para>
/// <para>
/// A failed refresh keeps the previous snapshot rather than emptying it. Losing the database should
/// not silently hand every feature back to its configured default — least of all the financial ones,
/// which an operator may have deliberately switched off.
/// </para>
/// </summary>
public sealed class FeatureOverrideStore : IFeatureOverrideStore
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FeatureOverrideStore> _logger;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private volatile IReadOnlyDictionary<string, bool> _snapshot =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public FeatureOverrideStore(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<FeatureOverrideStore> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, bool> Current
    {
        get
        {
            if (_timeProvider.GetUtcNow() - _loadedAt > StaleAfter)
            {
                // Fire and forget: the caller is answering a request and must not wait on a query.
                // It reads the slightly stale snapshot; the next one reads the fresh one.
                _ = RefreshAsync(CancellationToken.None);
            }

            return _snapshot;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // One reader at a time. Without this, a burst of requests arriving just after the snapshot
        // goes stale would each open a scope and run the same query.
        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            var db = scope.ServiceProvider.GetRequiredService<ISentinelDbContext>();

            var rows = await db.FeatureOverrides
                .AsNoTracking()
                .Select(entry => new { entry.Name, entry.IsEnabled })
                .ToListAsync(cancellationToken);

            _snapshot = rows.ToDictionary(
                row => row.Name, row => row.IsEnabled, StringComparer.OrdinalIgnoreCase);

            _loadedAt = _timeProvider.GetUtcNow();
        }
        catch (Exception ex)
        {
            // Keeps the previous snapshot. Stamped anyway so a database that is down does not make
            // every request retry the query.
            _loadedAt = _timeProvider.GetUtcNow();

            _logger.LogError(ex, "Could not read the feature switches; keeping the last known set.");
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
