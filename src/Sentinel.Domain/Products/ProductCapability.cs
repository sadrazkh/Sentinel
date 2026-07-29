namespace Sentinel.Domain.Products;

/// <summary>
/// What a product can do, as a set of independent flags.
/// <para>
/// This is what keeps product-specific behaviour out of the shared catalogue. A VPN needs
/// configurations and usage tracking; a desktop tool needs downloads; a game needs neither.
/// Branching on <see cref="ProductType"/> to work that out would scatter the same switch
/// across every service and view, and adding a product type would mean revisiting all of them.
/// </para>
/// <para>
/// Flags rather than a rule engine, deliberately: the question "can this be downloaded?" is a
/// property of the product, not a computation, and a bitmask stays type-safe, queryable in SQL
/// and obvious to read.
/// </para>
/// </summary>
[Flags]
public enum ProductCapability
{
    None = 0,

    /// <summary>Has plans and can be bought. Gated by the purchase feature flag.</summary>
    Purchasable = 1 << 0,

    /// <summary>An existing service can be extended rather than only bought afresh.</summary>
    Renewable = 1 << 1,

    /// <summary>Offers client downloads for one or more platforms.</summary>
    Downloadable = 1 << 2,

    /// <summary>Opens somewhere — a web app, a hosted tool. Requires a launch URL.</summary>
    Launchable = 1 << 3,

    HasDocumentation = 1 << 4,

    /// <summary>Issues per-user configuration material, such as VPN subscription entries.</summary>
    HasConfigurations = 1 << 5,

    /// <summary>Reports consumption — traffic, seats, calls.</summary>
    HasUsageTracking = 1 << 6,

    HasPlans = 1 << 7,

    /// <summary>Reachable only by members explicitly invited to its beta.</summary>
    BetaAccess = 1 << 8,

    /// <summary>Needs a live membership or service, not merely a one-off grant.</summary>
    RequiresActiveSubscription = 1 << 9,
}

public static class ProductCapabilityExtensions
{
    public static bool Has(this ProductCapability capabilities, ProductCapability capability) =>
        (capabilities & capability) == capability;

    /// <summary>The set an application-style product gets when nothing else is specified.</summary>
    public static readonly ProductCapability DefaultForWebApplication =
        ProductCapability.Launchable | ProductCapability.HasDocumentation;
}
