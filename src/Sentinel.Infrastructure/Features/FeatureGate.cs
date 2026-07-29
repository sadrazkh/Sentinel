using Microsoft.Extensions.Options;
using Sentinel.Application.Features;

namespace Sentinel.Infrastructure.Features;

public sealed class FeatureGate : IFeatureGate
{
    private readonly IOptionsMonitor<FeatureFlags> _flags;

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

    public FeatureGate(IOptionsMonitor<FeatureFlags> flags) => _flags = flags;

    public FeatureFlags Current => _flags.CurrentValue;

    /// <summary>
    /// An unknown feature name is treated as off. Failing closed means a typo or a renamed flag
    /// disables something rather than quietly leaving it open.
    /// </summary>
    public bool IsEnabled(string featureName) =>
        Accessors.TryGetValue(featureName, out var accessor) && accessor(_flags.CurrentValue);
}
