namespace Sentinel.Vpn.Plans;

/// <summary>
/// Which section of the VPN product page is being shown.
/// <para>
/// A typed enum bound from the route rather than a free string, so an unrecognised tab is a 404 at
/// the routing layer instead of a page that renders nothing.
/// </para>
/// </summary>
public enum VpnProductTab
{
    /// <summary>What the product is, and the plans on offer.</summary>
    Overview = 0,

    /// <summary>The member's own services. Today: their external subscription links.</summary>
    Services = 1,

    /// <summary>The individual configurations behind those services.</summary>
    Configurations = 2,

    /// <summary>Client applications, from the product's download list.</summary>
    Downloads = 3,

    /// <summary>Setup guides, from the product's documentation.</summary>
    Tutorials = 4,
}

/// <summary>
/// What each tab needs to know before it is offered.
/// <para>
/// A tab with nothing behind it is not shown. That is the difference between a page that adapts to
/// what exists and one that presents five headings, three of which lead nowhere.
/// </para>
/// </summary>
public sealed record VpnTabAvailability(
    bool HasPlans,
    int ServiceCount,
    int ConfigurationCount,
    int DownloadCount,
    int ArticleCount)
{
    public bool IsAvailable(VpnProductTab tab) => tab switch
    {
        // Always shown: it carries the product description even with no plans configured.
        VpnProductTab.Overview => true,

        VpnProductTab.Services => ServiceCount > 0,
        VpnProductTab.Configurations => ConfigurationCount > 0,
        VpnProductTab.Downloads => DownloadCount > 0,
        VpnProductTab.Tutorials => ArticleCount > 0,
        _ => false,
    };

    public int CountFor(VpnProductTab tab) => tab switch
    {
        VpnProductTab.Services => ServiceCount,
        VpnProductTab.Configurations => ConfigurationCount,
        VpnProductTab.Downloads => DownloadCount,
        VpnProductTab.Tutorials => ArticleCount,
        _ => 0,
    };
}
