using System.ComponentModel.DataAnnotations;

namespace Sentinel.Infrastructure.Subscriptions;

public sealed class SubscriptionFetchOptions
{
    public const string SectionName = "Subscriptions";

    /// <summary>Whether members and operators may add subscription links at all.</summary>
    public bool Enabled { get; set; } = true;

    [Range(2, 60)]
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Hard cap on the response body. A subscription is a few kilobytes of text; anything
    /// larger is either not a subscription or is trying to exhaust memory.
    /// </summary>
    [Range(1024, 4 * 1024 * 1024)]
    public int MaxResponseBytes { get; set; } = 512 * 1024;

    /// <summary>
    /// Redirects followed, each re-validated against the same rules as the original. Panels
    /// behind a CDN commonly use one or two.
    /// </summary>
    [Range(0, 5)]
    public int MaxRedirects { get; set; } = 3;

    /// <summary>
    /// How long a fetched result is reused before the upstream is asked again. Also the floor
    /// on how often one member can make the server issue outbound requests.
    /// </summary>
    [Range(1, 1440)]
    public int CacheMinutes { get; set; } = 15;

    /// <summary>How many sources one member may keep.</summary>
    [Range(1, 100)]
    public int MaxSourcesPerUser { get; set; } = 10;

    /// <summary>
    /// Sent as the User-Agent. Many panels return a different format — base64, plain, or a
    /// full client config — depending on this string, so it is configurable rather than
    /// hard-coded, and defaults to a value they all recognise.
    /// </summary>
    [StringLength(120)]
    public string UserAgent { get; set; } = "v2rayNG/1.8.29";
}
