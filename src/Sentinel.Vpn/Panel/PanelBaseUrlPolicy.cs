namespace Sentinel.Vpn.Panel;

public enum PanelUrlRejection
{
    None = 0,
    Empty = 1,
    NotAbsolute = 2,
    DisallowedScheme = 3,
    MissingHost = 4,
    EmbeddedCredentials = 5,
    HasQueryOrFragment = 6,
    TooLong = 7,
}

/// <summary>
/// What may be stored as a panel's base address.
/// <para>
/// An operator sets this, not a member — but it is still screened, and for a specific reason: the
/// portal makes server-side requests to it with a credential attached. An address pointing at the
/// cloud metadata service or an internal admin interface would turn the panel client into a way
/// to read the host's own secrets. The address policy at connect time is the control that holds;
/// this is the check that gives the operator a message instead of a silent failure.
/// </para>
/// <para>
/// A query string or fragment is refused because every API path is appended to this value —
/// <c>https://panel.example.com/?x=1</c> plus <c>/panel/api/…</c> is not a URL anyone meant.
/// </para>
/// </summary>
public static class PanelBaseUrlPolicy
{
    public const int MaxLength = 512;

    public static bool IsAllowed(string? baseUrl, bool allowInsecure = false) =>
        Validate(baseUrl, allowInsecure, out _) == PanelUrlRejection.None;

    public static PanelUrlRejection Validate(
        string? baseUrl,
        bool allowInsecure,
        out Uri? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return PanelUrlRejection.Empty;
        }

        if (baseUrl.Length > MaxLength)
        {
            return PanelUrlRejection.TooLong;
        }

        if (baseUrl.Any(char.IsControl))
        {
            // A raw tab or newline is stripped by some parsers before the URL is read, which
            // changes where it points.
            return PanelUrlRejection.NotAbsolute;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return PanelUrlRejection.NotAbsolute;
        }

        // Plain http would send the panel's API token in the clear on every call. Permitted only
        // when a deployment explicitly opts in — a panel reached over a private link, say — and
        // never by default.
        if (uri.Scheme != Uri.UriSchemeHttps
            && !(allowInsecure && uri.Scheme == Uri.UriSchemeHttp))
        {
            return PanelUrlRejection.DisallowedScheme;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return PanelUrlRejection.MissingHost;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return PanelUrlRejection.EmbeddedCredentials;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return PanelUrlRejection.HasQueryOrFragment;
        }

        parsed = uri;
        return PanelUrlRejection.None;
    }

    /// <summary>
    /// Joins the base address and an API path.
    /// <para>
    /// The path is always one of this assembly's own constants, never anything from a request, so
    /// there is no traversal to defend against — but the join still normalises the slashes, because
    /// an operator who pastes a trailing slash should not get a double one.
    /// </para>
    /// </summary>
    public static Uri Combine(Uri baseUrl, string apiPath)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        var prefix = baseUrl.AbsoluteUri.TrimEnd('/');
        var suffix = apiPath.TrimStart('/');

        return new Uri($"{prefix}/{suffix}", UriKind.Absolute);
    }
}
