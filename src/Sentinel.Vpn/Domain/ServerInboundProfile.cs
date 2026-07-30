using Sentinel.Domain.Common;

namespace Sentinel.Vpn.Domain;

/// <summary>
/// One inbound on a panel that the portal is allowed to attach clients to.
/// <para>
/// An allowlist, not a mirror. A panel usually carries inbounds the portal has no business
/// touching — an operator's own test inbound, something for a different tenant — and provisioning
/// against whatever happened to be there would be provisioning against a moving target. Nothing
/// gets used unless an operator added it here.
/// </para>
/// <para>
/// The inbound id is the panel's own integer, which is why it is not a <c>Guid</c>: it is a
/// foreign key into a system we do not own.
/// </para>
/// </summary>
public class ServerInboundProfile : IConcurrencyAware, ITimestamped
{
    public const int LabelMaxLength = 128;
    public const int ProtocolMaxLength = 32;
    public const int RemarkMaxLength = 256;

    public Guid Id { get; set; }

    public Guid ServerId { get; set; }

    public VpnServer? Server { get; set; }

    /// <summary>The panel's inbound id. Not ours; discovered from the panel and confirmed by an operator.</summary>
    public int InboundId { get; set; }

    /// <summary>What the portal calls it. Shown to operators, never to members.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Recorded from the panel — vless, vmess, trojan, shadowsocks, hysteria. Informational: the
    /// portal never sends a protocol, because the inbound already fixes it.
    /// </summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>The panel's own remark for the inbound, kept so an operator can match them up.</summary>
    public string? Remark { get; set; }

    /// <summary>
    /// Whether new clients may be attached here. Turning it off leaves existing clients alone,
    /// which is what makes draining an inbound possible without disrupting anyone.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public int DisplayOrder { get; set; }

    /// <summary>When the portal last confirmed this inbound still exists on the panel.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
