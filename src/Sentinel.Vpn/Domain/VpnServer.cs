using Sentinel.Domain.Common;

namespace Sentinel.Vpn.Domain;

public enum VpnServerStatus
{
    /// <summary>Configured but not yet verified. Never selected for a new service.</summary>
    Unverified = 0,

    Active = 1,

    /// <summary>Reachable, but withheld from new provisioning. Existing services keep working.</summary>
    Draining = 2,

    /// <summary>Deliberately withdrawn by an operator.</summary>
    Disabled = 3,

    /// <summary>Health checks are failing. Set by the system, cleared by the system.</summary>
    Unreachable = 4,
}

public enum VpnServerHealth
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Unreachable = 3,
}

/// <summary>
/// One 3x-ui panel the portal provisions against.
/// <para>
/// Several of these is the normal case, not the exception: a customer picks a country, and each
/// country is a different panel with its own address, credential and capacity. Nothing here is
/// shared between them, so adding a server is inserting a row.
/// </para>
/// </summary>
public class VpnServer : IConcurrencyAware, ITimestamped
{
    public const int KeyMaxLength = 64;
    public const int NameMaxLength = 128;
    public const int BaseUrlMaxLength = 512;
    public const int CountryCodeMaxLength = 2;
    public const int NotesMaxLength = 1000;

    public Guid Id { get; set; }

    /// <summary>Stable slug used in configuration and logs. Never shown to a member.</summary>
    public string Key { get; set; } = string.Empty;

    public string NameFa { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2, upper case. What a member actually chooses between.</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>
    /// The panel's origin plus any <c>webBasePath</c> it is mounted under — 3x-ui's API paths are
    /// relative to that prefix, so a panel behind one is unreachable without it.
    /// <para>
    /// Validated against the SSRF address policy before it is stored and again at connect time.
    /// </para>
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The panel's API token, encrypted at rest.
    /// <para>
    /// 3x-ui 3.x issues a static token under Settings → Security → API Token and accepts it as
    /// <c>Authorization: Bearer</c>. It is a full-control credential for that panel, so it is
    /// never stored in the clear, never logged, and never returned to a form once saved.
    /// </para>
    /// </summary>
    public string EncryptedApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Last four characters of the token, for an operator to confirm which credential is in place
    /// without the portal ever showing the whole thing again.
    /// </summary>
    public string? ApiTokenHint { get; set; }

    public VpnServerStatus Status { get; set; } = VpnServerStatus.Unverified;

    public VpnServerHealth Health { get; set; } = VpnServerHealth.Unknown;

    public DateTimeOffset? LastHealthCheckAt { get; set; }

    /// <summary>Short, already-redacted reason the last check failed. Never a raw exception.</summary>
    public string? LastHealthError { get; set; }

    // ---- capacity ----------------------------------------------------------------------

    /// <summary>
    /// How many customer services this server will hold. The ceiling an operator sets, not a
    /// measurement — the panel has no notion of what we consider full.
    /// </summary>
    public int MaxClients { get; set; }

    /// <summary>
    /// Services currently counted against <see cref="MaxClients"/>, including reservations that
    /// have not finished provisioning. Maintained by the capacity service, never by hand.
    /// </summary>
    public int ReservedClients { get; set; }

    /// <summary>Lower is preferred when several servers can take a new service.</summary>
    public int SelectionPriority { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }

    public ICollection<ServerInboundProfile> InboundProfiles { get; set; } = new List<ServerInboundProfile>();

    // ---- derived -----------------------------------------------------------------------

    /// <summary>Whether a new service may be placed here at all.</summary>
    public bool AcceptsNewServices =>
        Status == VpnServerStatus.Active
        && Health != VpnServerHealth.Unreachable
        && RemainingCapacity > 0;

    public int RemainingCapacity => Math.Max(0, MaxClients - ReservedClients);

    /// <summary>
    /// Load as a fraction, for choosing between servers that can all take the work. Guards the
    /// divide: a server with no ceiling set reads as full rather than as infinitely free.
    /// </summary>
    public double LoadFactor => MaxClients <= 0 ? 1d : (double)ReservedClients / MaxClients;
}
