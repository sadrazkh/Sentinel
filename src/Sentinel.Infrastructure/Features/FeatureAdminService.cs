using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Common;
using Sentinel.Application.Features;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Domain.Settings;

namespace Sentinel.Infrastructure.Features;

/// <summary>
/// Moves the feature switches.
/// <para>
/// Every change is audited with who moved it and which way, because turning a feature on is how a
/// whole area of the portal appears for every member at once — and turning one off is how it
/// disappears. That is the sort of thing somebody asks about a week later.
/// </para>
/// </summary>
public sealed class FeatureAdminService : IFeatureAdminService
{
    private readonly ISentinelDbContext _db;
    private readonly FeatureGate _gate;
    private readonly IFeatureOverrideStore _overrides;
    private readonly IAuditService _audit;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FeatureAdminService> _logger;

    public FeatureAdminService(
        ISentinelDbContext db,
        IFeatureGate gate,
        IFeatureOverrideStore overrides,
        IAuditService audit,
        TimeProvider timeProvider,
        ILogger<FeatureAdminService> logger)
    {
        _db = db;

        // The concrete gate, for the configured-value reading the screen shows beside each switch.
        _gate = (FeatureGate)gate;
        _overrides = overrides;
        _audit = audit;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FeatureState>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.FeatureOverrides
            .AsNoTracking()
            .ToDictionaryAsync(entry => entry.Name, cancellationToken);

        return FeatureGate.KnownFeatures
            .Select(name =>
            {
                var configured = _gate.ConfiguredValue(name);

                // Matched case-insensitively, like the gate: a row written with different casing
                // must not read as a separate feature.
                var overridden = rows.FirstOrDefault(
                    entry => string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

                return overridden is null
                    ? new FeatureState(name, configured, configured, FeatureSource.Configuration, null, null)
                    : new FeatureState(
                        name,
                        overridden.IsEnabled,
                        configured,
                        FeatureSource.Override,
                        overridden.UpdatedAt,
                        overridden.UpdatedByUserId);
            })
            .ToList();
    }

    public async Task<OperationResult> SetAsync(
        string featureName,
        bool? enabled,
        Guid performedByUserId,
        CancellationToken cancellationToken = default)
    {
        // Only names this build actually has. An override for a feature that no longer exists would
        // sit in the table for ever, and one for an invented name would let a form post create rows.
        var canonical = FeatureGate.KnownFeatures.FirstOrDefault(
            known => string.Equals(known, featureName, StringComparison.OrdinalIgnoreCase));

        if (canonical is null)
        {
            return OperationResult.Failure(FeatureErrors.UnknownFeature);
        }

        var existing = await _db.FeatureOverrides
            .FirstOrDefaultAsync(entry => entry.Name == canonical, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var before = _gate.IsEnabled(canonical);

        if (enabled is null)
        {
            // Handing the switch back to the deployment. Removing the row is the only deletion in
            // this table and it is the point of it — a row saying "false" cannot express "follow
            // configuration", which is a different and useful state.
            if (existing is not null)
            {
                _db.FeatureOverrides.Remove(existing);
            }
        }
        else if (existing is null)
        {
            _db.FeatureOverrides.Add(new FeatureOverride
            {
                Id = SequentialGuid.New(now),
                Name = canonical,
                IsEnabled = enabled.Value,
                UpdatedByUserId = performedByUserId,
            });
        }
        else
        {
            existing.IsEnabled = enabled.Value;
            existing.UpdatedByUserId = performedByUserId;
        }

        await _audit.RecordAsync(
            AuditEntry.For(
                enabled is null ? FeatureAuditActions.Reset : FeatureAuditActions.Changed,
                nameof(FeatureOverride),
                canonical) with
            {
                ActorUserIdOverride = performedByUserId,
                Metadata = AuditMetadata.Create()
                    .Set("feature", canonical)
                    .Set("configuredValue", _gate.ConfiguredValue(canonical))
                    .SetChange("enabled", before, enabled ?? _gate.ConfiguredValue(canonical)),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        // Immediately, so the operator's next page load reflects what they just did rather than
        // waiting out the snapshot's staleness window.
        await _overrides.RefreshAsync(cancellationToken);

        _logger.LogInformation(
            "Feature {Feature} set to {State} by {UserId}.",
            canonical,
            enabled is null ? "configured default" : enabled.Value.ToString(),
            performedByUserId);

        return OperationResult.Success();
    }
}
