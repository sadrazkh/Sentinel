using Sentinel.Domain.Common;

namespace Sentinel.Vpn.Domain;

/// <summary>
/// Where a service is in its life.
/// <para>
/// Stored, unlike <c>MembershipStatus</c>, because it is not derivable from dates alone: whether a
/// client actually exists on a panel is a fact about the outside world, and the difference between
/// "we have not provisioned it yet" and "we tried and do not know" is the whole point of
/// <see cref="NeedsAttention"/>.
/// </para>
/// </summary>
public enum CustomerServiceStatus
{
    /// <summary>Recorded, not yet sent to a panel.</summary>
    Pending = 0,

    /// <summary>A provisioning job is in flight.</summary>
    Provisioning = 1,

    Active = 2,

    /// <summary>Withheld by an operator. The client is disabled on the panel but not deleted.</summary>
    Suspended = 3,

    /// <summary>Past its expiry date.</summary>
    Expired = 4,

    /// <summary>Traffic allowance used up.</summary>
    Exhausted = 5,

    /// <summary>Being removed from the panel.</summary>
    Decommissioning = 6,

    /// <summary>Gone. The row survives for the record; the panel client does not.</summary>
    Ended = 7,

    /// <summary>
    /// A panel operation ended without a usable answer, so nobody knows whether it took effect.
    /// Never retried automatically — the reconciliation sweep establishes the truth first.
    /// </summary>
    NeedsAttention = 8,
}

/// <summary>
/// One member's provisioned VPN service.
/// <para>
/// The terms are <em>copied</em> from the plan when the service is created, not read through a
/// reference. A plan is an operator's price list and changes; what a customer was sold does not.
/// Reading the live plan would silently re-price or re-quota every existing service the moment
/// somebody edited it.
/// </para>
/// </summary>
public class CustomerService : IConcurrencyAware, ITimestamped
{
    public const int NotesMaxLength = 1000;
    public const int ErrorMaxLength = 500;

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Which catalogue product this belongs to, so the library can show it.</summary>
    public Guid ProductId { get; set; }

    /// <summary>
    /// The plan it was bought on, for the record. Nullable because a plan may later be deleted and
    /// that must not take a live service with it.
    /// </summary>
    public Guid? PlanId { get; set; }

    /// <summary>A snapshot of the plan's name, so the service still reads correctly if the plan goes.</summary>
    public string PlanNameFa { get; set; } = string.Empty;

    public string PlanNameEn { get; set; } = string.Empty;

    // ---- placement ---------------------------------------------------------------------

    /// <summary>
    /// The panel currently serving this service. Chosen by
    /// <see cref="Provisioning.ServerSelector"/> — never supplied by a member, which is why there is
    /// no request shape carrying a server id.
    /// </summary>
    public Guid? ServerId { get; set; }

    public VpnServer? Server { get; set; }

    /// <summary>
    /// The opaque identifier this service has on the panel, minted by
    /// <see cref="Panel.PanelClientEmail"/>. Not the member's real address.
    /// </summary>
    public string? PanelClientEmail { get; set; }

    // ---- terms, copied from the plan ---------------------------------------------------

    /// <summary>Bytes. Zero means unlimited, matching the panel's convention.</summary>
    public long TrafficBytes { get; set; }

    public int DeviceLimit { get; set; }

    public CustomerServiceStatus Status { get; set; } = CustomerServiceStatus.Pending;

    public DateTimeOffset? StartsAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    // ---- usage, synced from the panel --------------------------------------------------

    public long UsedBytes { get; set; }

    public DateTimeOffset? LastUsageSyncAt { get; set; }

    public DateTimeOffset? LastOnlineAt { get; set; }

    /// <summary>
    /// The token in this service's delivery URL, hashed.
    /// <para>
    /// This is what an incoming request is matched against. The URL is a capability — anyone holding
    /// it gets the member's configurations without signing in, because a VPN client application
    /// cannot sign in — so what the request path compares must be a digest, not the secret.
    /// </para>
    /// </summary>
    public string? DeliveryTokenHash { get; set; }

    /// <summary>
    /// The same token, sealed with the data-protection key ring, so its owner can read their own URL
    /// again. See <see cref="Delivery.IDeliverySecretProtector"/> for why both forms are kept.
    /// </summary>
    public string? DeliveryTokenSealed { get; set; }

    public DateTimeOffset? DeliveryTokenIssuedAt { get; set; }

    /// <summary>Short, already-redacted reason the last operation failed. Never a raw exception.</summary>
    public string? LastError { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }

    public ICollection<ServiceInboundBinding> Bindings { get; set; } = new List<ServiceInboundBinding>();

    // ---- derived -----------------------------------------------------------------------

    public bool IsUnlimitedTraffic => TrafficBytes <= 0;

    public long? RemainingBytes =>
        IsUnlimitedTraffic ? null : Math.Max(0, TrafficBytes - UsedBytes);

    /// <summary>
    /// Whether the member should currently be able to connect.
    /// <para>
    /// Deliberately not the same as <c>Status == Active</c>: the status is what the last sweep
    /// recorded, and a service can pass its expiry between sweeps. Both are checked so a stale
    /// status never keeps a finished service serving.
    /// </para>
    /// </summary>
    public bool IsUsableAt(DateTimeOffset instant) =>
        Status == CustomerServiceStatus.Active
        && (ExpiresAt is not { } expires || expires > instant)
        && (IsUnlimitedTraffic || UsedBytes < TrafficBytes);
}

/// <summary>
/// Which panel inbound a service's client is attached to, and whether we have confirmed it.
/// <para>
/// A child table rather than columns on the service, for one reason that matters: during a migration
/// a client is briefly attached on two servers at once. Columns could not express that, and the
/// window where both are live is exactly the state that has to be visible.
/// </para>
/// </summary>
public class ServiceInboundBinding : IConcurrencyAware, ITimestamped
{
    public Guid Id { get; set; }

    public Guid ServiceId { get; set; }

    public CustomerService? Service { get; set; }

    public Guid ServerId { get; set; }

    /// <summary>The panel's own inbound id — a foreign key into a system we do not own.</summary>
    public int InboundId { get; set; }

    public BindingState State { get; set; } = BindingState.Pending;

    /// <summary>When the portal last saw this binding on the panel with its own eyes.</summary>
    public DateTimeOffset? LastVerifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}

public enum BindingState
{
    /// <summary>Intended, not yet sent.</summary>
    Pending = 0,

    /// <summary>Confirmed present on the panel.</summary>
    Attached = 1,

    /// <summary>Confirmed absent.</summary>
    Detached = 2,

    /// <summary>
    /// A write about this binding ended without an answer. It may or may not exist on the panel, so
    /// nothing may assume either — reconciliation looks before anything else acts.
    /// </summary>
    Unknown = 3,
}
