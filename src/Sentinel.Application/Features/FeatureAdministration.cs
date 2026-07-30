using Sentinel.Application.Common;

namespace Sentinel.Application.Features;

/// <summary>How a feature came to be on or off, which is what an operator needs to see.</summary>
public enum FeatureSource
{
    /// <summary>The value the deployment shipped with.</summary>
    Configuration = 0,

    /// <summary>Somebody moved the switch in the back office.</summary>
    Override = 1,
}

/// <summary>One switch as the back office reads it.</summary>
public sealed record FeatureState(
    string Name,
    bool IsEnabled,
    bool ConfiguredValue,
    FeatureSource Source,
    DateTimeOffset? ChangedAt,
    Guid? ChangedByUserId)
{
    /// <summary>
    /// Whether an operator's switch currently disagrees with the deployment. Worth showing: it is
    /// the difference between "this is how we run" and "somebody changed this on Tuesday".
    /// </summary>
    public bool DivergesFromConfiguration => Source == FeatureSource.Override
                                             && IsEnabled != ConfiguredValue;
}

/// <summary>
/// Reads and moves the feature switches.
/// <para>
/// Separate from <see cref="IFeatureGate"/> on purpose. The gate is asked a question thousands of
/// times a request and answers from memory; this is asked rarely and writes. Keeping them apart
/// means nothing on a hot path can accidentally take a database dependency.
/// </para>
/// </summary>
public interface IFeatureAdminService
{
    Task<IReadOnlyList<FeatureState>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a switch. <paramref name="enabled"/> of <c>null</c> removes the override, handing the
    /// feature back to whatever the deployment configured.
    /// </summary>
    Task<OperationResult> SetAsync(
        string featureName,
        bool? enabled,
        Guid performedByUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The switch positions the gate reads.
/// <para>
/// A singleton holding a snapshot, because <see cref="IFeatureGate.IsEnabled"/> is called on every
/// request that touches a gated endpoint and must not open a database connection to answer. The
/// snapshot is refreshed after a write and on a short timer, so a change made on one replica
/// reaches the others without anything being broadcast between them.
/// </para>
/// </summary>
public interface IFeatureOverrideStore
{
    /// <summary>The current overrides. Never <c>null</c>; an empty map means configuration wins.</summary>
    IReadOnlyDictionary<string, bool> Current { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public static class FeatureErrors
{
    public const string UnknownFeature = "admin.error.featureUnknown";
}

public static class FeatureAuditActions
{
    public const string Changed = "feature.changed";
    public const string Reset = "feature.reset";
}
