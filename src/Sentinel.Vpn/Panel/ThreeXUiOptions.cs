using System.ComponentModel.DataAnnotations;

namespace Sentinel.Vpn.Panel;

public sealed class ThreeXUiOptions
{
    public const string SectionName = "Vpn:Panel";

    /// <summary>
    /// Short. A panel call sits on a request path or a provisioning job, and a panel that has not
    /// answered in this long is not about to.
    /// </summary>
    [Range(2, 60)]
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Hard cap on a response body. The inbound list is the largest thing the portal reads, and a
    /// panel that returns something enormous should be refused rather than buffered.
    /// </summary>
    [Range(4_096, 16 * 1024 * 1024)]
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// Whether a panel may be addressed over plain http.
    /// <para>
    /// Off. Turning it on sends the panel's API token unencrypted on every call, so it exists only
    /// for a deployment where the panel is reached over a private link — and
    /// <c>SentinelOptionsValidation</c> refuses it in Production.
    /// </para>
    /// </summary>
    public bool AllowInsecurePanelUrls { get; set; }

    /// <summary>
    /// Whether the panel client may reach loopback.
    /// <para>
    /// Off, and off in every real deployment: it exists so the integration suite can point the
    /// client at a fake panel on localhost. A production value of <c>true</c> would let an operator
    /// aim the client at the portal's own host.
    /// </para>
    /// </summary>
    public bool AllowLoopbackPanelUrls { get; set; }

    /// <summary>How often the health sweep re-checks each server. Zero disables the sweep.</summary>
    [Range(0, 1440)]
    public int HealthCheckIntervalMinutes { get; set; } = 5;
}
