using Microsoft.Extensions.DependencyInjection;
using Sentinel.Vpn.Panel;
using Sentinel.Vpn.Plans;
using Sentinel.Vpn.Servers;

namespace Sentinel.Vpn;

/// <summary>
/// The VPN module's own registrations.
/// <para>
/// One entry point, so the host adds the module in a line and the module decides its own
/// lifetimes. The credential protector is deliberately <em>not</em> registered here: its
/// implementation depends on the web host's data-protection key ring, which this assembly does not
/// reference.
/// </para>
/// </summary>
public static class VpnModule
{
    public static IServiceCollection AddSentinelVpn(this IServiceCollection services)
    {
        // One client for the process. It owns a pooled handler carrying the connect-time address
        // validation, and creating one per request would throw the connection pool away with it —
        // which for a TLS handshake per panel call is expensive.
        services.AddSingleton<IThreeXUiClient, ThreeXUiClient>();

        services.AddScoped<IVpnServerAdminService, VpnServerAdminService>();
        services.AddScoped<IVpnServerAdminQuery, VpnServerAdminQuery>();

        services.AddScoped<IServicePlanCatalog, ServicePlanCatalogService>();
        services.AddScoped<IServicePlanAdminService, ServicePlanAdminService>();
        services.AddScoped<IServicePlanAdminQuery, ServicePlanAdminQuery>();

        // Moves a dead panel out of selection before a customer's provisioning meets it.
        services.AddHostedService<ServerHealthService>();

        return services;
    }
}
