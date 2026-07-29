namespace Sentinel.Domain.Products;

/// <summary>
/// Shapes a product's presentation and nothing else.
/// <para>
/// Behaviour comes from <see cref="ProductCapability"/>. This exists so the catalogue can group
/// and label things, not so services can branch on it.
/// </para>
/// </summary>
public enum ProductType
{
    /// <summary>Sold as an ongoing service with quota or time. VPN lives here.</summary>
    SubscriptionService = 1,

    WebApplication = 2,
    DesktopApplication = 3,
    MobileApplication = 4,
    Game = 5,
    DigitalTool = 6,

    /// <summary>Several products offered together.</summary>
    Bundle = 7,
}

/// <summary>
/// Where a product is in its life, from the operator's point of view.
/// <para>
/// Ordered from least to most public so "is this at least X?" comparisons work. Only
/// <see cref="Stable"/> and <see cref="Deprecated"/> are unconditionally listable; the earlier
/// stages need an invitation, and the later ones are withdrawn.
/// </para>
/// </summary>
public enum ProductReleaseStatus
{
    /// <summary>Internal only. Never appears to a member, whatever their entitlements.</summary>
    Draft = 0,

    /// <summary>Visible only to members holding an explicit grant.</summary>
    PrivatePreview = 1,

    Alpha = 2,
    Beta = 3,
    Stable = 4,

    /// <summary>Still usable by existing holders, but no longer promoted or sold.</summary>
    Deprecated = 5,

    /// <summary>Advertised as a teaser. Listed, never opened.</summary>
    ComingSoon = 6,

    /// <summary>Withdrawn. Hidden from the catalogue; existing grants keep their history.</summary>
    Archived = 7,
}

/// <summary>
/// What a specific member's relationship to a product is right now. Computed, never stored.
/// </summary>
public enum ProductAccessStatus
{
    /// <summary>Not available to this member and not offered to them.</summary>
    Locked = 0,

    /// <summary>Bought outright; no expiry to worry about.</summary>
    Owned = 1,

    /// <summary>A live service or subscription is running.</summary>
    Active = 2,

    Trial = 3,

    /// <summary>Granted by an operator rather than bought — a loyalty perk.</summary>
    Gifted = 4,

    BetaAccess = 5,

    /// <summary>Was available and has run out. Distinct from Locked: renewing would fix it.</summary>
    Expired = 6,

    AvailableToBuy = 7,

    ComingSoon = 8,
}

/// <summary>
/// The single button a product card leads with.
/// <para>
/// Decided on the server. A card that worked out its own action from a handful of booleans
/// would eventually disagree with what the endpoints actually permit, and the disagreement
/// would be a member clicking Open on something they may not open.
/// </para>
/// </summary>
public enum ProductPrimaryAction
{
    None = 0,
    ViewDetails = 1,
    Open = 2,
    Download = 3,
    Manage = 4,
    Buy = 5,
    Renew = 6,
    JoinBeta = 7,
    ComingSoon = 8,
}
