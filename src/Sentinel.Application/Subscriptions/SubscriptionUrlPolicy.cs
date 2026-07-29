using System.Diagnostics.CodeAnalysis;

namespace Sentinel.Application.Subscriptions;

public enum SubscriptionUrlRejection
{
    None = 0,
    Empty = 1,
    NotAbsolute = 2,
    DisallowedScheme = 3,
    MissingHost = 4,
    EmbeddedCredentials = 5,
    TooLong = 6,
    /// <summary>The host is a literal address the connection policy would refuse anyway.</summary>
    DisallowedHost = 7,
    NonStandardPort = 8,
}

/// <summary>
/// What may be stored and fetched as a subscription source.
/// <para>
/// The first of two layers. This one screens the URL as written — scheme, credentials, obvious
/// internal hostnames — and rejects bad input at the point somebody types it. It is not
/// sufficient on its own: a hostname can resolve anywhere, and can resolve differently between
/// this check and the connection. That is what <see cref="IpAddressPolicy"/> handles, applied
/// at connect time.
/// </para>
/// </summary>
public static class SubscriptionUrlPolicy
{
    public const int MaxLength = 2048;

    /// <summary>
    /// Ports the fetcher is allowed to reach. Restricting them keeps the portal from being
    /// used to probe internal services on unusual ports even when the host itself resolves
    /// publicly.
    /// </summary>
    private static readonly int[] AllowedPorts = [80, 443, 8080, 8443, 2053, 2083, 2087, 2096];

    /// <summary>Host names that never legitimately serve a customer's subscription.</summary>
    private static readonly string[] BlockedHostNames =
    [
        "localhost",
        "localhost.localdomain",
        "metadata",
        "metadata.google.internal",
        "instance-data",
    ];

    public static bool IsAllowed(string? url) => Validate(url, out _) == SubscriptionUrlRejection.None;

    public static SubscriptionUrlRejection Validate(string? url, [NotNullWhen(true)] out Uri? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            return SubscriptionUrlRejection.Empty;
        }

        if (url.Length > MaxLength)
        {
            return SubscriptionUrlRejection.TooLong;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var candidate))
        {
            return SubscriptionUrlRejection.NotAbsolute;
        }

        // http and https only. Everything else — file:, ftp:, gopher:, dict: — exists in SSRF
        // write-ups precisely because a permissive fetcher will happily follow it.
        if (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)
        {
            return SubscriptionUrlRejection.DisallowedScheme;
        }

        if (string.IsNullOrEmpty(candidate.Host))
        {
            return SubscriptionUrlRejection.MissingHost;
        }

        // user:password@ would be sent as credentials to whatever the host turns out to be.
        if (!string.IsNullOrEmpty(candidate.UserInfo))
        {
            return SubscriptionUrlRejection.EmbeddedCredentials;
        }

        if (BlockedHostNames.Contains(candidate.Host, StringComparer.OrdinalIgnoreCase))
        {
            return SubscriptionUrlRejection.DisallowedHost;
        }

        // A literal address is checked here as well as at connect time, so an obviously
        // internal target is rejected while the operator is still looking at the form rather
        // than failing later with a vaguer message.
        if (System.Net.IPAddress.TryParse(candidate.Host.Trim('[', ']'), out var literal)
            && !IpAddressPolicy.IsAllowed(literal))
        {
            return SubscriptionUrlRejection.DisallowedHost;
        }

        if (!AllowedPorts.Contains(candidate.Port))
        {
            return SubscriptionUrlRejection.NonStandardPort;
        }

        parsed = candidate;
        return SubscriptionUrlRejection.None;
    }
}
