using System.Globalization;

namespace Sentinel.Application.Subscriptions;

/// <summary>
/// Reads the <c>subscription-userinfo</c> header, whose format is
/// <c>upload=0; download=123; total=1000; expire=1735689600</c>.
/// <para>
/// This header is the only dependable way to know a subscription has run out. The remark text
/// often carries something like "9.97GB-29D" too, but that is decoration a provider formats
/// however it likes; the header is a de-facto standard every mainstream panel emits.
/// </para>
/// </summary>
public static class SubscriptionUserInfoParser
{
    public const string HeaderName = "subscription-userinfo";

    public static SubscriptionUserInfo Parse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return SubscriptionUserInfo.Empty;
        }

        long? upload = null;
        long? download = null;
        long? total = null;
        DateTimeOffset? expires = null;

        foreach (var part in headerValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);

            if (pair.Length != 2)
            {
                continue;
            }

            var key = pair[0].Trim().ToLowerInvariant();
            var rawValue = pair[1].Trim();

            if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            switch (key)
            {
                case "upload":
                    upload = Sanitize(number);
                    break;

                case "download":
                    download = Sanitize(number);
                    break;

                case "total":
                    // Panels use 0 for "unlimited"; treating it as a real quota would show
                    // every such subscription as permanently exhausted.
                    total = number > 0 ? number : null;
                    break;

                case "expire":
                    expires = ParseExpiry(number);
                    break;
            }
        }

        return new SubscriptionUserInfo(upload, download, total, expires);
    }

    /// <summary>
    /// The expiry is a Unix timestamp in seconds. 0 means "no expiry" on every panel that
    /// emits this header, and an out-of-range value is discarded rather than allowed to throw.
    /// </summary>
    private static DateTimeOffset? ParseExpiry(long unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static long? Sanitize(long value) => value >= 0 ? value : null;
}
