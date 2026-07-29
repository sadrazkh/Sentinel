namespace Sentinel.Application.Notifications;

/// <summary>
/// What may be stored as a notification's destination.
/// <para>
/// The value becomes something the member clicks — a link in the portal and, once delivered, a
/// link inside a Telegram message. An absolute URL here would be an open redirect handed
/// straight to them, with the portal's own name on it. Only local paths survive, and the rule
/// lives here as a pure function so it can be tested exhaustively and reused by both the
/// writer and the redirect that eventually follows it.
/// </para>
/// </summary>
public static class NotificationLinkPolicy
{
    public const int MaxLength = 512;

    public static bool IsAllowed(string? linkPath) => Sanitize(linkPath) is not null;

    /// <summary>Returns the path if it is safe to store, or <c>null</c> if it is not.</summary>
    public static string? Sanitize(string? linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath))
        {
            return null;
        }

        var trimmed = linkPath.Trim();

        if (trimmed.Length > MaxLength)
        {
            return null;
        }

        // One leading slash and no second one: "//host" is protocol-relative and leaves the
        // site, and "/\host" is the same trick with the slash browsers also accept.
        if (!trimmed.StartsWith('/')
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/\\", StringComparison.Ordinal))
        {
            return null;
        }

        // Backslashes and traversal segments are refused outright rather than normalised:
        // normalising invites an argument about whether the normaliser matches the one the
        // routing layer will use later.
        if (trimmed.Contains('\\', StringComparison.Ordinal)
            || trimmed.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        // A control character would let a stored value break out of the attribute or header it
        // is later written into.
        foreach (var character in trimmed)
        {
            if (char.IsControl(character))
            {
                return null;
            }
        }

        return trimmed;
    }
}
