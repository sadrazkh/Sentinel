using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Sentinel.Vpn.Panel;

namespace Sentinel.IntegrationTests.Infrastructure;

/// <summary>
/// A host wired for provisioning tests.
/// <para>
/// Two deliberate substitutions. The panel client becomes a <see cref="ScriptedPanel"/>, so a test can
/// force an unknown outcome on exactly the call it cares about. And the background workers are
/// removed, so the tests drive the executor and the reconciler by hand — a timer firing halfway
/// through an assertion is the classic source of a suite that passes four times out of five.
/// </para>
/// </summary>
public sealed class VpnTestFactory : SentinelWebApplicationFactory
{
    /// <summary>The panel every test in a class shares. Registered as a singleton so it keeps state.</summary>
    public ScriptedPanel Panel { get; } = new();

    protected override void ConfigureTestSettings(IWebHostBuilder builder)
    {
        // Off, so nothing runs on a timer. Every sweep in these tests is invoked explicitly.
        builder.UseSetting("Vpn:Provisioning:Enabled", "false");

        builder.ConfigureServices(services =>
        {
            // The real client is a singleton owning a pooled handler; replacing the registration
            // rather than adding one keeps a single implementation in the container.
            services.RemoveAll<IThreeXUiClient>();
            services.AddSingleton<IThreeXUiClient>(Panel);

            // Belt and braces: the option above already stops it, but a hosted service that slipped
            // through would make these tests flaky in a way that is painful to diagnose.
            services.RemoveAll<IHostedService>();
        });
    }
}
