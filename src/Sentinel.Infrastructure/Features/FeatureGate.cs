using Microsoft.Extensions.Options;
using Sentinel.Application.Features;

namespace Sentinel.Infrastructure.Features;

/// <summary>
/// Answers whether a feature is on.
/// <para>
/// Two layers, in order: a switch an operator has set, then the value the deployment configured.
/// An operator's switch wins because it is the more recent decision and the one taken with the
/// system in front of them — but only where one exists, so a feature nobody has touched still
/// follows the deployment.
/// </para>
/// </summary>
public sealed class FeatureGate : IFeatureGate
{
    private readonly IOptionsMonitor<FeatureFlags> _flags;
    private readonly IFeatureOverrideStore _overrides;

    // Reflection once at type load rather than per call: the property set is fixed at compile
    // time, and a feature check sits on request paths.
    private static readonly Dictionary<string, Func<FeatureFlags, bool>> Accessors =
        typeof(FeatureFlags)
            .GetProperties()
            .Where(property => property.PropertyType == typeof(bool))
            .ToDictionary(
                property => property.Name,
                property => (Func<FeatureFlags, bool>)(flags => (bool)property.GetValue(flags)!),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>Every switch this build knows about, in declaration order.</summary>
    public static IReadOnlyList<string> KnownFeatures { get; } =
        typeof(FeatureFlags)
            .GetProperties()
            .Where(property => property.PropertyType == typeof(bool))
            .Select(property => property.Name)
            .ToList();

    public FeatureGate(IOptionsMonitor<FeatureFlags> flags, IFeatureOverrideStore overrides)
    {
        _flags = flags;
        _overrides = overrides;
    }

    public FeatureFlags Current => _flags.CurrentValue;

    /// <summary>The value the deployment configured, ignoring any override. For the admin screen.</summary>
    public bool ConfiguredValue(string featureName) =>
        Accessors.TryGetValue(featureName, out var accessor) && accessor(_flags.CurrentValue);

    /// <summary>
    /// An unknown feature name is treated as off. Failing closed means a typo or a renamed flag
    /// disables something rather than quietly leaving it open — and an override naming a feature
    /// this build no longer has is ignored for the same reason.
    /// </summary>
    public bool IsEnabled(string featureName)
    {
        if (!Accessors.TryGetValue(featureName, out var accessor))
        {
            return false;
        }

        return _overrides.Current.TryGetValue(featureName, out var overridden)
            ? overridden
            : accessor(_flags.CurrentValue);
    }
}
