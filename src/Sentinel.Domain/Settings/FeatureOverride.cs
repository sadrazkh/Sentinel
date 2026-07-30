using Sentinel.Domain.Common;

namespace Sentinel.Domain.Settings;

/// <summary>
/// A feature switch an operator has set, overriding the value that shipped in configuration.
/// <para>
/// Only features live here — not connection strings, keys or anything else from the secret store.
/// A switch is an operating decision somebody makes at four in the afternoon when a panel is down;
/// a credential is a deployment concern. Giving the first a screen does not mean giving the second
/// one, and the system page stays read-only for exactly that reason.
/// </para>
/// <para>
/// The absence of a row means "whatever configuration says". That is deliberate: an operator can
/// take their hands off a switch and have it follow the deployment again, which is not something a
/// row holding <c>false</c> could express.
/// </para>
/// </summary>
public class FeatureOverride : IConcurrencyAware, ITimestamped
{
    public const int NameMaxLength = 64;

    public Guid Id { get; set; }

    /// <summary>The property name on <c>FeatureFlags</c>, matched case-insensitively.</summary>
    public string Name { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    /// <summary>Who last moved it. Every change is also an audit row; this is for the screen.</summary>
    public Guid? UpdatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
