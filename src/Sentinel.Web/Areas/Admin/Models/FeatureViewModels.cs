using Sentinel.Application.Features;

namespace Sentinel.Web.Areas.Admin.Models;

public sealed class FeatureListViewModel
{
    public required IReadOnlyList<FeatureState> Features { get; init; }

    /// <summary>
    /// Features that let money move or a member commit to a purchase. Grouped and labelled apart
    /// from the rest because the consequence of a mistake is different in kind: a wrongly-enabled
    /// catalogue shows somebody a page they should not see, a wrongly-enabled wallet lets credit
    /// change hands.
    /// </summary>
    public static readonly IReadOnlySet<string> Financial = new HashSet<string>(
        [FeatureNames.Wallet, FeatureNames.Purchases, FeatureNames.Payments],
        StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<FeatureState> Portal =>
        Features.Where(feature => !Financial.Contains(feature.Name)).ToList();

    public IReadOnlyList<FeatureState> Money =>
        Features.Where(feature => Financial.Contains(feature.Name)).ToList();

    /// <summary>Whether the three switches the VPN flow needs are all on.</summary>
    public bool VpnFlowOpen =>
        IsOn(FeatureNames.VpnSelfService) && IsOn(FeatureNames.Wallet) && IsOn(FeatureNames.Purchases);

    private bool IsOn(string name) =>
        Features.Any(feature =>
            string.Equals(feature.Name, name, StringComparison.OrdinalIgnoreCase)
            && feature.IsEnabled);
}
