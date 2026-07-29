namespace Sentinel.Application.Features;

/// <summary>
/// Named switches for whole areas of the portal.
/// <para>
/// The financial ones default to <c>false</c>. Money handling is the part of this system where
/// a mistake costs a customer something real, so it stays off until it has been reviewed
/// deliberately rather than arriving switched on because a default said so.
/// </para>
/// </summary>
public sealed class FeatureFlags
{
    public const string SectionName = "Features";

    /// <summary>The product library as a whole: My Library and product detail pages.</summary>
    public bool ProductLibraryEnabled { get; set; } = true;

    /// <summary>Browsing products the member does not yet have.</summary>
    public bool ProductDiscoveryEnabled { get; set; } = true;

    public bool ProductDocumentationEnabled { get; set; } = true;

    /// <summary>Whether products in alpha or beta are listed at all.</summary>
    public bool BetaProductsEnabled { get; set; } = true;

    /// <summary>Members managing their own VPN services rather than an operator doing it.</summary>
    public bool VpnSelfServiceEnabled { get; set; }

    /// <summary>Ordering a plan. Off until the purchase flow has been reviewed.</summary>
    public bool PurchasesEnabled { get; set; }

    /// <summary>Any online payment path. Off; the first version has no gateway at all.</summary>
    public bool PaymentsEnabled { get; set; }

    /// <summary>The credit ledger. Off until reviewed.</summary>
    public bool WalletEnabled { get; set; }

    /// <summary>Members adding their own external subscription links.</summary>
    public bool ExternalSubscriptionsEnabled { get; set; } = true;
}

/// <summary>
/// The names an endpoint or view can ask about. Constants rather than raw strings so a typo is
/// a compile error instead of a feature that is silently always off.
/// </summary>
public static class FeatureNames
{
    public const string ProductLibrary = nameof(FeatureFlags.ProductLibraryEnabled);
    public const string ProductDiscovery = nameof(FeatureFlags.ProductDiscoveryEnabled);
    public const string ProductDocumentation = nameof(FeatureFlags.ProductDocumentationEnabled);
    public const string BetaProducts = nameof(FeatureFlags.BetaProductsEnabled);
    public const string VpnSelfService = nameof(FeatureFlags.VpnSelfServiceEnabled);
    public const string Purchases = nameof(FeatureFlags.PurchasesEnabled);
    public const string Payments = nameof(FeatureFlags.PaymentsEnabled);
    public const string Wallet = nameof(FeatureFlags.WalletEnabled);
    public const string ExternalSubscriptions = nameof(FeatureFlags.ExternalSubscriptionsEnabled);
}

/// <summary>
/// Answers whether a feature is on.
/// <para>
/// Injected wherever the answer is needed — including services, not only controllers — because
/// hiding a menu item is not what disabling a feature means. A feature that is off must also
/// refuse its endpoints and its background work, or it is merely invisible.
/// </para>
/// </summary>
public interface IFeatureGate
{
    bool IsEnabled(string featureName);

    FeatureFlags Current { get; }
}
