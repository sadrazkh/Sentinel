using System.Text.RegularExpressions;

namespace Sentinel.Vpn.Panel;

/// <summary>
/// The identifier the portal gives a client on a panel.
/// <para>
/// Not the member's real e-mail address, despite the panel's field being called that. Two reasons:
/// a customer's address would be copied into a third-party system and shown in its UI, and it would
/// let the same person's services on different servers collide. Instead the portal mints an opaque
/// identifier per service.
/// </para>
/// <para>
/// The shape is fixed and checked on the way out, because this value lands in a URL path on every
/// panel call. Generating it rather than accepting one removes path traversal, injection into the
/// panel's own storage, and collisions in a single decision.
/// </para>
/// </summary>
public static partial class PanelClientEmail
{
    /// <summary>Kept short: it appears in the panel's UI, where an operator has to read it.</summary>
    public const int TokenLength = 16;

    private const string Prefix = "s-";

    [GeneratedRegex("^s-[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>
    /// Mints an identifier for a service.
    /// <para>
    /// Derived from a random <see cref="Guid"/> rather than from the service id: a panel is a third
    /// party, and putting our own primary keys into it hands over more than it needs.
    /// </para>
    /// </summary>
    public static string Create() => Prefix + Guid.NewGuid().ToString("N")[..TokenLength];

    /// <summary>
    /// Whether a value is one this portal generated.
    /// <para>
    /// Checked before every panel call, not only on creation: the value arrives from our database,
    /// and a row that predates this rule — or came from a restore — must not be able to steer a
    /// request path.
    /// </para>
    /// </summary>
    public static bool IsValid(string? email) =>
        !string.IsNullOrEmpty(email) && Pattern().IsMatch(email);
}
