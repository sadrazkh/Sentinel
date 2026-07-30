using Sentinel.Vpn.Domain;

namespace Sentinel.Web.Infrastructure;

/// <summary>
/// Maps the VPN module's enums onto localisation keys and badge styles. One file, so a new enum
/// member fails to compile here rather than rendering as a blank badge in three templates.
/// </summary>
public static class VpnPresentation
{
    public static string StatusKey(VpnServerStatus status) => $"vpnStatus.{Lower(status)}";

    public static string HealthKey(VpnServerHealth health) => $"vpnHealth.{Lower(health)}";

    public static string StatusBadgeClass(VpnServerStatus status) => status switch
    {
        VpnServerStatus.Active => "badge--success",
        VpnServerStatus.Draining => "badge--warning",
        VpnServerStatus.Unverified => "badge--info",
        VpnServerStatus.Unreachable => "badge--danger",
        VpnServerStatus.Disabled => "badge--neutral",
        _ => "badge--neutral",
    };

    public static string HealthBadgeClass(VpnServerHealth health) => health switch
    {
        VpnServerHealth.Healthy => "badge--success",
        VpnServerHealth.Degraded => "badge--warning",
        VpnServerHealth.Unreachable => "badge--danger",
        _ => "badge--neutral",
    };

    /// <summary>Matches the lower-case key convention the rest of the admin views already use.</summary>
    private static string Lower<TEnum>(TEnum value) where TEnum : struct, Enum =>
        value.ToString()!.ToLowerInvariant();
}
