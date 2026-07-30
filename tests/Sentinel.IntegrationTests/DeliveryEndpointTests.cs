using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.IntegrationTests.Infrastructure;
using Sentinel.Vpn.Delivery;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Panel;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Plans;
using Sentinel.Vpn.Provisioning;
using Sentinel.Vpn.Servers;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The anonymous subscription endpoint, over real HTTP.
/// <para>
/// This is the one surface of the portal an outsider can reach without an account, and the token in
/// the path is the entire authorisation. So the tests here are mostly about what it refuses and how
/// it refuses: unknown, malformed and revoked all have to look identical, or the endpoint becomes a
/// way of learning which tokens exist.
/// </para>
/// </summary>
public sealed class DeliveryEndpointTests : IClassFixture<VpnTestFactory>, IAsyncLifetime
{
    private const long FiftyGibibytes = 53_687_091_200L;
    private const string PanelToken = "integration-only-panel-token-13579";

    private readonly VpnTestFactory _factory;
    private readonly HttpClient _client;

    public DeliveryEndpointTests(VpnTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateNonRedirectingClient();
    }

    public Task InitializeAsync()
    {
        _factory.Panel.AllCallsUnknown = false;
        _factory.Panel.ClearScripts();

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------------------- fixtures ----

    /// <summary>A live service and the plaintext token that reaches it.</summary>
    private async Task<(Guid ServiceId, string Token)> LiveServiceAsync(string prefix)
    {
        var userId = await _factory.CreateMemberAsync($"{prefix}-member");
        var productId = await _factory.CreateProductAsync($"{prefix}-product");

        var serverId = await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();

            var created = await admin.SaveAsync(null, new VpnServerSaveRequest(
                $"{prefix}-server", "سرور", "Server", "DE",
                "https://panel.example.com:2053", PanelToken,
                VpnServerStatus.Active, 10, 100, null, null));

            Assert.True(created.Succeeded, created.ErrorKey);

            var db = services.GetRequiredService<IVpnDbContext>();

            var server = await db.VpnServers.FirstAsync(candidate => candidate.Id == created.Value);
            server.Status = VpnServerStatus.Active;
            server.Health = VpnServerHealth.Healthy;

            db.ServerInboundProfiles.Add(new ServerInboundProfile
            {
                Id = Guid.NewGuid(),
                ServerId = created.Value,
                InboundId = 1,
                Label = "vless:443",
                Protocol = "vless",
                IsEnabled = true,
            });

            await db.SaveChangesAsync();

            return created.Value;
        });

        Assert.NotEqual(Guid.Empty, serverId);

        var planId = await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IServicePlanAdminService>();

            var result = await admin.SaveAsync(null, new ServicePlanSaveRequest(
                $"{prefix}-plan", productId, "پلن", "Plan", null, null,
                FiftyGibibytes, 30, 2, 1_000_000, "IRR",
                true, true, null, 100, false, null));

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

        var serviceId = await _factory.WithScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<ICustomerServiceManager>()
                .CreateAsync(new CreateServiceRequest(userId, planId, null));

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

        // Run the queued provisioning job so the client actually exists on the fake panel.
        await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IProvisioningExecutor>().RunPendingAsync(20));

        // The sealed copy is how the owner's own page reads the link back; the test reads it the
        // same way, which also proves seal-and-open round-trips through the database.
        var token = await _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<IVpnDbContext>();
            var secrets = services.GetRequiredService<IDeliverySecretProtector>();

            var sealedToken = await db.CustomerServices
                .AsNoTracking()
                .Where(service => service.Id == serviceId)
                .Select(service => service.DeliveryTokenSealed)
                .FirstAsync();

            return secrets.Open(sealedToken);
        });

        Assert.NotNull(token);

        return (serviceId, token!);
    }

    private Task MutateAsync(Guid serviceId, Action<CustomerService> change) =>
        _factory.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<IVpnDbContext>();

            var service = await db.CustomerServices
                .FirstAsync(candidate => candidate.Id == serviceId);

            change(service);

            await db.SaveChangesAsync();
        });

    // ------------------------------------------------------------------------- happy path ----

    [Fact]
    public async Task A_live_service_serves_its_configurations_as_a_subscription()
    {
        var (_, token) = await LiveServiceAsync("del-live");

        var response = await _client.GetAsync($"/s/{token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        // A subscription body is base64 of newline-separated URIs — what a client application reads.
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(body));

        Assert.Contains("vless://", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_plain_form_returns_the_uris_unencoded()
    {
        var (_, token) = await LiveServiceAsync("del-plain");

        var response = await _client.GetAsync($"/s/{token}/plain");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("vless://", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_response_is_never_cached()
    {
        // The body is one member's own credentials. A shared cache holding it would serve them to
        // whoever asked next, which is the worst failure this endpoint could have.
        var (_, token) = await LiveServiceAsync("del-nostore");

        var response = await _client.GetAsync($"/s/{token}");

        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.True(response.Headers.CacheControl?.Private);
    }

    [Fact]
    public async Task It_needs_no_sign_in()
    {
        // The client used here has never authenticated, which is the whole premise: a VPN client
        // application polling this URL has no way to sign in.
        var (_, token) = await LiveServiceAsync("del-anon");

        using var stranger = _factory.CreateNonRedirectingClient();

        Assert.Equal(HttpStatusCode.OK, (await stranger.GetAsync($"/s/{token}")).StatusCode);
    }

    // --------------------------------------------------------------------------- refusals ----

    [Theory]
    [InlineData("short")]
    [InlineData("Zg")]
    // The right length, wrong alphabet.
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    // Path traversal dressed as a token.
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    // A SQL fragment, to show the value never reaches a query as text.
    [InlineData("' OR 1=1 --")]
    public async Task A_malformed_token_is_indistinguishable_from_an_unknown_one(string token)
    {
        var response = await _client.GetAsync($"/s/{token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_well_formed_token_nobody_holds_is_a_plain_not_found()
    {
        var (unknown, _) = DeliveryToken.Create();

        var response = await _client.GetAsync($"/s/{unknown}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Bare, not the portal's branded error page. This URL is designed to say nothing about where
        // it leads, and a full HTML page naming the portal in the member's language would undo that.
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rotating_kills_the_previous_link_immediately()
    {
        var (serviceId, token) = await LiveServiceAsync("del-rotate");

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/s/{token}")).StatusCode);

        var ownerId = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .Where(service => service.Id == serviceId)
                .Select(service => service.UserId)
                .FirstAsync());

        var replacement = await _factory.WithScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<ICustomerServiceManager>()
                .RotateDeliveryTokenAsync(serviceId, ownerId);

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

        // The old one now answers exactly as an invented one does.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/s/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/s/{replacement}")).StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_rotate_another_members_link()
    {
        var (serviceId, token) = await LiveServiceAsync("del-notyours");
        var strangerId = await _factory.CreateMemberAsync("del-notyours-stranger");

        var result = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ICustomerServiceManager>()
                .RotateDeliveryTokenAsync(serviceId, strangerId));

        // Not-found rather than forbidden: a member probing ids must not learn which ones exist.
        Assert.False(result.Succeeded);
        Assert.Equal(ServiceErrors.NotFound, result.ErrorKey);

        // And the real owner's link is untouched.
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/s/{token}")).StatusCode);
    }

    // ----------------------------------------------------------------------- lapsed service ----

    [Fact]
    public async Task An_expired_service_answers_gone_rather_than_not_found()
    {
        // 410 tells a client application to stop polling. 404 would leave it retrying for ever, and
        // the holder of a valid token already knows the service exists — so this is not a disclosure.
        var (serviceId, token) = await LiveServiceAsync("del-expired");

        await MutateAsync(serviceId, service =>
            service.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(HttpStatusCode.Gone, (await _client.GetAsync($"/s/{token}")).StatusCode);
    }

    [Fact]
    public async Task A_service_past_its_quota_answers_gone()
    {
        var (serviceId, token) = await LiveServiceAsync("del-exhausted");

        await MutateAsync(serviceId, service => service.UsedBytes = service.TrafficBytes);

        Assert.Equal(HttpStatusCode.Gone, (await _client.GetAsync($"/s/{token}")).StatusCode);
    }

    [Fact]
    public async Task A_suspended_service_stops_serving_at_once()
    {
        // The panel client is only disabled when the queued job runs. This endpoint must not wait for
        // that — an operator who suspends a service expects it to stop now.
        var (serviceId, token) = await LiveServiceAsync("del-suspended");

        await MutateAsync(serviceId, service => service.Status = CustomerServiceStatus.Suspended);

        Assert.Equal(HttpStatusCode.Gone, (await _client.GetAsync($"/s/{token}")).StatusCode);
    }

    [Fact]
    public async Task Expiry_is_honoured_the_moment_it_passes_not_at_the_next_sweep()
    {
        // The stored status still says Active — nothing has run to change it. The endpoint recomputes
        // usability against the clock on every request, which is what makes that safe.
        var (serviceId, token) = await LiveServiceAsync("del-clock");

        await MutateAsync(serviceId, service =>
        {
            service.Status = CustomerServiceStatus.Active;
            service.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        });

        Assert.Equal(HttpStatusCode.Gone, (await _client.GetAsync($"/s/{token}")).StatusCode);
    }

    // ---------------------------------------------------------------------------- panel down ----

    [Fact]
    public async Task A_panel_that_cannot_be_read_is_a_retryable_failure()
    {
        var (_, token) = await LiveServiceAsync("del-panel-down");

        _factory.Panel.ScriptOnce("links", PanelOutcome.Blocked);

        var response = await _client.GetAsync($"/s/{token}");

        // 503 with Retry-After, not 410: the service is fine, the panel is not, and a client told
        // "gone" would give up on a subscription that is perfectly valid.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task An_unknown_outcome_from_the_panel_never_becomes_a_delivery()
    {
        var (_, token) = await LiveServiceAsync("del-panel-unknown");

        _factory.Panel.ScriptOnce("links", PanelOutcome.UnknownOutcome);

        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await _client.GetAsync($"/s/{token}")).StatusCode);
    }

    // ------------------------------------------------------------------------ decommissioned ----

    [Fact]
    public async Task Decommissioning_revokes_the_link_before_the_panel_is_even_told()
    {
        var (serviceId, token) = await LiveServiceAsync("del-decommission");

        await _factory.WithScopeAsync(services =>
            services.GetRequiredService<ICustomerServiceManager>().DecommissionAsync(serviceId));

        // The queued job has deliberately not been run. Whoever holds this URL must stop being served
        // the moment the service is withdrawn, not whenever the panel gets round to answering.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/s/{token}")).StatusCode);
    }
}
