using System.Diagnostics.CodeAnalysis;

namespace Sentinel.Application.Catalog;

public enum ApplicationUrlRejection
{
    None = 0,
    Empty = 1,
    NotAbsolute = 2,
    /// <summary>Anything that is not http or https — <c>javascript:</c>, <c>data:</c>, <c>file:</c>…</summary>
    DisallowedScheme = 3,
    /// <summary>Plain http to a non-loopback host.</summary>
    InsecureScheme = 4,
    /// <summary>Contains <c>user:password@</c>, which would put a credential in the browser history.</summary>
    EmbeddedCredentials = 5,
    MissingHost = 6,
    TooLong = 7,
}

/// <summary>
/// What may be stored as an application's launch destination.
/// <para>
/// Enforced both when an administrator saves a URL and again at launch time. The second check
/// is not redundant: a row could predate this rule or arrive through a database restore, and
/// the launch endpoint issues a redirect the browser will follow — the one place where a
/// <c>javascript:</c> URL would actually execute.
/// </para>
/// </summary>
public static class ApplicationUrlPolicy
{
    public const int MaxLength = 2048;

    public static bool IsAllowed(string? url) => Validate(url, out _) == ApplicationUrlRejection.None;

    public static ApplicationUrlRejection Validate(string? url, [NotNullWhen(true)] out Uri? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            return ApplicationUrlRejection.Empty;
        }

        if (url.Length > MaxLength)
        {
            return ApplicationUrlRejection.TooLong;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var candidate))
        {
            // Relative or malformed. A relative destination would resolve against the portal
            // itself, which is never what an external application launch means.
            return ApplicationUrlRejection.NotAbsolute;
        }

        if (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)
        {
            return ApplicationUrlRejection.DisallowedScheme;
        }

        if (string.IsNullOrEmpty(candidate.Host))
        {
            return ApplicationUrlRejection.MissingHost;
        }

        if (!string.IsNullOrEmpty(candidate.UserInfo))
        {
            return ApplicationUrlRejection.EmbeddedCredentials;
        }

        // Plain http is tolerated only for loopback, which is how a developer points an entry
        // at something running on their own machine. Anything reachable over a network must
        // be https, or the redirect hands the member's session to the first observer on the path.
        if (candidate.Scheme == Uri.UriSchemeHttp && !candidate.IsLoopback)
        {
            return ApplicationUrlRejection.InsecureScheme;
        }

        parsed = candidate;
        return ApplicationUrlRejection.None;
    }
}
