using Sentinel.Domain.Common;

namespace Sentinel.Vpn.Domain;

/// <summary>
/// A purchasable shape of VPN service: how much traffic, for how long, on how many devices.
/// <para>
/// Every number that decides what a customer gets lives here, set by an operator. Nothing on this
/// entity is ever taken from a request — a member picks a plan by id and the portal reads the terms
/// from the row. A quota or a duration arriving in a form would be a customer choosing their own
/// price.
/// </para>
/// <para>
/// Money is stored in minor units as a whole number. A binary <c>decimal</c> is fine for arithmetic
/// but invites rounding conversations at every boundary; an integer count of the smallest unit has
/// exactly one interpretation.
/// </para>
/// </summary>
public class ServicePlan : IConcurrencyAware, ITimestamped
{
    public const int KeyMaxLength = 64;
    public const int NameMaxLength = 128;
    public const int DescriptionMaxLength = 1000;
    public const int CurrencyMaxLength = 3;

    public Guid Id { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The catalogue product this plan belongs to. A plan is never free-floating: it is one way to
    /// buy one product, and the product is what carries the description, docs and downloads.
    /// </summary>
    public Guid ProductId { get; set; }

    public string NameFa { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    public string? DescriptionFa { get; set; }

    public string? DescriptionEn { get; set; }

    // ---- what the customer gets ---------------------------------------------------------

    /// <summary>
    /// Traffic allowance in bytes. Zero means unlimited — the same convention 3x-ui uses, so the
    /// value passes to the panel without a translation step that could invert its meaning.
    /// </summary>
    public long TrafficBytes { get; set; }

    /// <summary>How long the service runs from the moment it is provisioned.</summary>
    public int DurationDays { get; set; }

    /// <summary>Simultaneous devices. Zero means the panel imposes no limit.</summary>
    public int DeviceLimit { get; set; }

    // ---- price -------------------------------------------------------------------------

    /// <summary>Minor units — rials, or cents. Zero is a legitimate price for a trial plan.</summary>
    public long PriceMinorUnits { get; set; }

    /// <summary>ISO 4217, upper case. Stored per plan so a second currency does not need a migration.</summary>
    public string Currency { get; set; } = "IRR";

    // ---- availability ------------------------------------------------------------------

    /// <summary>Whether the plan appears in the catalogue at all.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Whether it can be ordered, as distinct from displayed.
    /// <para>
    /// Two separate switches because "shown but not orderable" is a real state: a plan being
    /// retired stays visible to the people already on it while taking no new orders. Purchasing
    /// also needs the <c>PurchasesEnabled</c> feature, so this alone never opens a checkout.
    /// </para>
    /// </summary>
    public bool IsPurchasable { get; set; }

    /// <summary>
    /// Restrict the plan to one country, or leave <c>null</c> for any server.
    /// <para>
    /// A code rather than a server id: servers come and go, and a plan tied to a specific machine
    /// would break the first time that machine was replaced.
    /// </para>
    /// </summary>
    public string? CountryCode { get; set; }

    public int DisplayOrder { get; set; }

    /// <summary>Pinned as the recommended option. At most one per product, enforced on save.</summary>
    public bool IsFeatured { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }

    public ICollection<PlanAudienceRule> AudienceRules { get; set; } = new List<PlanAudienceRule>();

    // ---- derived -----------------------------------------------------------------------

    public bool IsUnlimitedTraffic => TrafficBytes <= 0;

    public bool IsUnlimitedDevices => DeviceLimit <= 0;

    public bool IsFree => PriceMinorUnits <= 0;
}
