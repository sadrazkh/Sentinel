using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Domain.Identity;
using Sentinel.IntegrationTests.Infrastructure;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Servers;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The VPN server admin over its real HTTP surface. The security question here is different from
/// the rest of the admin area: these screens hold a credential that grants full control of a
/// third-party panel, so what must never happen is that credential reaching a page.
/// </summary>
public sealed class VpnServerAdminTests : IClassFixture<SentinelWebApplicationFactory>
{
    /// <summary>Obviously synthetic and used only by this suite.</summary>
    private const string PanelToken = "integration-only-panel-token-13579";

    private readonly SentinelWebApplicationFactory _factory;

    public VpnServerAdminTests(SentinelWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminClientAsync(string userName)
    {
        await _factory.CreateMemberAsync(userName);
        await _factory.AddToRoleAsync(userName, RoleNames.Admin);

        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    private Task<Guid> CreateServerAsync(string key, string country = "DE") =>
        _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();

            var result = await admin.SaveAsync(null, new VpnServerSaveRequest(
                key,
                $"سرور {key}",
                $"Server {key}",
                country,
                "https://panel.example.com:2053",
                PanelToken,
                VpnServerStatus.Unverified,
                MaxClients: 200,
                SelectionPriority: 100,
                Notes: null,
                ConcurrencyToken: null));

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

    // ------------------------------------------------------------------- authorization ----

    [Fact]
    public async Task An_ordinary_member_cannot_reach_the_vpn_server_admin()
    {
        await _factory.CreateMemberAsync("vpn-admin-member");

        using var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync("vpn-admin-member", PortalTestData.MemberPassword);

        var response = await client.GetAsync("/Admin/VpnServers");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Support_can_look_but_cannot_add_or_contact_a_panel()
    {
        // Probing is a write even though it changes nothing an operator typed: it makes an
        // authenticated outbound request, which a read-only role must not be able to trigger.
        await _factory.CreateMemberAsync("vpn-admin-support");
        await _factory.AddToRoleAsync("vpn-admin-support", RoleNames.Support);

        var serverId = await CreateServerAsync("support-view");

        using var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync("vpn-admin-support", PortalTestData.MemberPassword);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/VpnServers")).StatusCode);

        foreach (var path in new[] { "/Admin/VpnServers/new", $"/Admin/VpnServers/{serverId}" })
        {
            var refused = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
            Assert.Contains(
                "/Account/AccessDenied",
                refused.Headers.Location?.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_anonymous_visitor_is_sent_to_the_login_page()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/Admin/VpnServers");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/Login",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------- the credential ----

    [Fact]
    public async Task The_panel_token_never_appears_on_any_admin_page()
    {
        // The whole point of storing it encrypted with only a hint kept. A form that round-tripped
        // it would put a full-control credential into the page source and the browser's autofill.
        using var admin = await AdminClientAsync("vpn-token-leak");
        var serverId = await CreateServerAsync("token-leak");

        foreach (var path in new[]
                 {
                     "/Admin/VpnServers",
                     $"/Admin/VpnServers/{serverId}",
                     $"/Admin/VpnServers/{serverId}/inbounds",
                 })
        {
            var page = await admin.GetStringAsync(path);

            Assert.DoesNotContain(PanelToken, page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_list_shows_only_the_tail_of_the_stored_token()
    {
        using var admin = await AdminClientAsync("vpn-token-hint");
        await CreateServerAsync("token-hint");

        var page = await admin.GetStringAsync("/Admin/VpnServers");

        // Four characters is enough for an operator to tell which credential is in place, and not
        // enough to be one.
        Assert.Contains("····" + PanelToken[^4..], page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_without_a_token_keeps_the_stored_one()
    {
        // What makes it possible to change a server's capacity without re-typing a credential
        // nobody can read back.
        var serverId = await CreateServerAsync("keep-token");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();

            var result = await admin.SaveAsync(serverId, new VpnServerSaveRequest(
                "keep-token", "سرور", "Server", "DE", "https://panel.example.com:2053",
                ApiToken: null, VpnServerStatus.Unverified, 500, 100, null, null));

            Assert.True(result.Succeeded, result.ErrorKey);

            // The endpoint still resolves, which it only can if the token decrypted.
            var endpoint = await admin.ResolveEndpointAsync(serverId);

            Assert.NotNull(endpoint);
            Assert.Equal(PanelToken, endpoint!.ApiToken);
        });
    }

    [Fact]
    public void An_endpoint_never_prints_its_token()
    {
        // These get logged. A record's default ToString would include every property.
        var endpoint = new Sentinel.Vpn.Panel.PanelEndpoint("https://panel.example.com", PanelToken);

        Assert.DoesNotContain(PanelToken, endpoint.ToString(), StringComparison.Ordinal);
        Assert.Contains("panel.example.com", endpoint.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------- validation ----

    [Theory]
    [InlineData("http://panel.example.com")]
    [InlineData("panel.example.com")]
    [InlineData("https://user:secret@panel.example.com")]
    [InlineData("https://panel.example.com/?x=1")]
    [InlineData("ftp://panel.example.com")]
    public async Task A_panel_address_that_fails_the_policy_is_refused(string baseUrl) =>
        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();

            var result = await admin.SaveAsync(null, new VpnServerSaveRequest(
                "bad-url", "سرور", "Server", "DE", baseUrl, PanelToken,
                VpnServerStatus.Unverified, 100, 100, null, null));

            Assert.False(result.Succeeded, $"'{baseUrl}' should have been refused.");
            Assert.Equal(VpnServerErrors.BaseUrlInvalid, result.ErrorKey);
        });

    [Fact]
    public async Task A_new_server_requires_a_token() =>
        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();

            var result = await admin.SaveAsync(null, new VpnServerSaveRequest(
                "no-token", "سرور", "Server", "DE", "https://panel.example.com",
                ApiToken: null, VpnServerStatus.Unverified, 100, 100, null, null));

            Assert.False(result.Succeeded);
            Assert.Equal(VpnServerErrors.TokenRequired, result.ErrorKey);
        });

    [Theory]
    [InlineData("D")]
    [InlineData("DEU")]
    [InlineData("D1")]
    public async Task A_malformed_country_code_is_refused(string country) =>
        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();

            var result = await admin.SaveAsync(null, new VpnServerSaveRequest(
                $"bad-country-{country}", "سرور", "Server", country,
                "https://panel.example.com", PanelToken,
                VpnServerStatus.Unverified, 100, 100, null, null));

            Assert.False(result.Succeeded);
            Assert.Equal(VpnServerErrors.CountryInvalid, result.ErrorKey);
        });

    [Fact]
    public async Task A_duplicate_key_is_refused()
    {
        await CreateServerAsync("duplicate-key");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();

            var result = await admin.SaveAsync(null, new VpnServerSaveRequest(
                "duplicate-key", "سرور", "Server", "NL", "https://other.example.com",
                PanelToken, VpnServerStatus.Unverified, 100, 100, null, null));

            Assert.False(result.Succeeded);
            Assert.Equal(VpnServerErrors.KeyTaken, result.ErrorKey);
        });
    }

    [Fact]
    public async Task Changing_the_address_puts_an_active_server_back_to_unverified()
    {
        // Otherwise selection would keep placing services on a panel nobody has reached since it
        // was reconfigured.
        var serverId = await CreateServerAsync("reverify");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();
            var query = services.GetRequiredService<IVpnServerAdminQuery>();

            // Force it Active first, as a successful probe would.
            await admin.SaveAsync(serverId, new VpnServerSaveRequest(
                "reverify", "سرور", "Server", "DE", "https://panel.example.com:2053",
                null, VpnServerStatus.Active, 100, 100, null, null));

            await admin.SaveAsync(serverId, new VpnServerSaveRequest(
                "reverify", "سرور", "Server", "DE", "https://moved.example.com:2053",
                null, VpnServerStatus.Active, 100, 100, null, null));

            var after = await query.GetForEditAsync(serverId);

            Assert.Equal(VpnServerStatus.Unverified, after!.Status);
        });
    }

    // ------------------------------------------------------------------------- probing ----

    [Fact]
    public async Task Probing_an_unreachable_panel_records_it_rather_than_throwing()
    {
        // panel.example.com is the IANA documentation domain and answers nothing, which is exactly
        // the condition the health path has to survive.
        var serverId = await CreateServerAsync("unreachable");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();
            var query = services.GetRequiredService<IVpnServerAdminQuery>();

            var result = await admin.ProbeAsync(serverId);

            Assert.True(result.Succeeded);
            Assert.False(result.Value!.Reachable);
            Assert.Equal(VpnServerHealth.Unreachable, result.Value.Health);

            // Still Unverified, not Unreachable: this panel has never been reached, and
            // "unreachable" would imply it once worked. Either way it is out of selection.
            var after = await query.GetForEditAsync(serverId);
            Assert.Equal(VpnServerStatus.Unverified, after!.Status);
        });
    }

    [Fact]
    public async Task A_failed_probe_takes_a_working_server_out_of_selection()
    {
        // The transition that matters: a panel that was serving customers and has now died must
        // stop receiving new services before anyone's provisioning meets it.
        var serverId = await CreateServerAsync("was-working");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();
            var query = services.GetRequiredService<IVpnServerAdminQuery>();

            // Promoted to Active the way a successful probe would leave it.
            await admin.SaveAsync(serverId, new VpnServerSaveRequest(
                "was-working", "سرور", "Server", "DE", "https://panel.example.com:2053",
                null, VpnServerStatus.Active, 100, 100, null, null));

            var before = await query.GetForEditAsync(serverId);
            Assert.Equal(VpnServerStatus.Active, before!.Status);

            await admin.ProbeAsync(serverId);

            var after = await query.GetForEditAsync(serverId);
            Assert.Equal(VpnServerStatus.Unreachable, after!.Status);
        });
    }

    [Fact]
    public async Task An_operator_disabled_server_is_not_overruled_by_a_failed_probe()
    {
        // The sweep reports; it does not undo a decision an operator made.
        var serverId = await CreateServerAsync("stays-disabled");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();
            var query = services.GetRequiredService<IVpnServerAdminQuery>();

            await admin.SaveAsync(serverId, new VpnServerSaveRequest(
                "stays-disabled", "سرور", "Server", "DE", "https://panel.example.com:2053",
                null, VpnServerStatus.Disabled, 100, 100, null, null));

            await admin.ProbeAsync(serverId);

            var after = await query.GetForEditAsync(serverId);
            Assert.Equal(VpnServerStatus.Disabled, after!.Status);
        });
    }

    // -------------------------------------------------------------------------- pages ----

    [Fact]
    public async Task The_admin_pages_render_in_both_languages()
    {
        using var admin = await AdminClientAsync("vpn-pages");
        var serverId = await CreateServerAsync("render-check");

        foreach (var culture in new[] { "fa-IR", "en-US" })
        {
            foreach (var path in new[]
                     {
                         "/Admin/VpnServers",
                         "/Admin/VpnServers/new",
                         $"/Admin/VpnServers/{serverId}",
                         $"/Admin/VpnServers/{serverId}/inbounds",
                     })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Add("Accept-Language", culture);

                using var response = await admin.SendAsync(request);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var page = await response.Content.ReadAsStringAsync();

                // A raw key on the page means a missing translation.
                Assert.DoesNotContain("vpnStatus.", page, StringComparison.Ordinal);
                Assert.DoesNotContain("admin.vpn.", page, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task A_server_with_no_inbound_is_flagged_as_unusable()
    {
        // Active with zero allowlisted inbounds is a configuration that silently never gets
        // selected, so the list says so instead of leaving an operator to infer it.
        var serverId = await CreateServerAsync("no-inbounds");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();

            await admin.SaveAsync(serverId, new VpnServerSaveRequest(
                "no-inbounds", "سرور", "Server", "DE", "https://panel.example.com:2053",
                null, VpnServerStatus.Active, 100, 100, null, null));
        });

        var flagged = await _factory.WithScopeAsync(async services =>
        {
            var query = services.GetRequiredService<IVpnServerAdminQuery>();
            var servers = await query.ListAsync();

            return servers.Single(server => server.Key == "no-inbounds").IsMisconfigured;
        });

        Assert.True(flagged);
    }
}
