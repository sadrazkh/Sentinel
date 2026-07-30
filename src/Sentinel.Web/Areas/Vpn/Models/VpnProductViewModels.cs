using Sentinel.Application.Products;
using Sentinel.Application.Subscriptions;
using Sentinel.Vpn.Plans;

namespace Sentinel.Web.Areas.Vpn.Models;

/// <summary>
/// The VPN product page.
/// <para>
/// Composed from three existing sources rather than a new one: the shared product library for
/// access and content, the subscription feature for the member's own services, and the VPN module
/// for plans. Nothing here re-implements what those already decide.
/// </para>
/// </summary>
public sealed class VpnProductViewModel
{
    public required ProductDetail Detail { get; init; }

    public required VpnProductTab ActiveTab { get; init; }

    public required VpnTabAvailability Tabs { get; init; }

    public required ServicePlanCatalog Plans { get; init; }

    /// <summary>
    /// The member's own services. Today these are their external subscription links; managed
    /// services provisioned against a panel join this list in the provisioning phase.
    /// </summary>
    public required IReadOnlyList<SubscriptionView> Services { get; init; }

    public required ProductPageContent Content { get; init; }

    public required string TimeZoneId { get; init; }

    /// <summary>
    /// Whether members may manage their own services. Off, so the page is read-only: it shows what
    /// somebody has and what is on offer, and ordering waits for the purchase flow.
    /// </summary>
    public required bool SelfServiceEnabled { get; init; }

    public string ProductKey => Detail.Card.Key;

    /// <summary>Every configuration across every service, for the configurations tab.</summary>
    public IReadOnlyList<(string ServiceTitle, Application.Subscriptions.ProxyConfig Config)> AllConfigurations =>
        Services
            .SelectMany(service => service.Configs.Select(config => (service.Title, config)))
            .ToList();
}
