using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Domain.Identity;
using Sentinel.IntegrationTests.Infrastructure;
using Sentinel.Vpn.Delivery;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Plans;
using Sentinel.Vpn.Provisioning;
using Sentinel.Vpn.Servers;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The customer-service screens over their real HTTP surface.
/// <para>
/// Two things are worth proving here beyond "the page renders". First, that reading and changing are
/// separated: support can see whether a service is working without being able to end it. Second,
/// that neither the delivery token nor a panel credential can reach an operator's page — those
/// belong to the member and to the server respectively, and an operator needs neither to do the job.
/// </para>
/// </summary>
public sealed class CustomerServiceAdminTests : IClassFixture<VpnTestFactory>, IAsyncLifetime
{
    private const long FiftyGibibytes = 53_687_091_200L;
    private const string PanelToken = "integration-only-panel-token-24680";

    private readonly VpnTestFactory _factory;

    public CustomerServiceAdminTests(VpnTestFactory factory) => _factory = factory;

    public Task InitializeAsync()
    {
        _factory.Panel.AllCallsUnknown = false;
        _factory.Panel.ClearScripts();

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------------------- fixtures ----

    private async Task<HttpClient> ClientAsync(string userName, string? role = null)
    {
        await _factory.CreateMemberAsync(userName);

        if (role is not null)
        {
            await _factory.AddToRoleAsync(userName, role);
        }

        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);

        return client;
    }

    private Task<Guid> CreateServerAsync(string key) =>
        _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IVpnServerAdminService>();

            var created = await admin.SaveAsync(null, new VpnServerSaveRequest(
                key, $"سرور {key}", $"Server {key}", "DE",
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

    private Task<Guid> CreatePlanAsync(Guid productId, string key) =>
        _factory.WithScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<IServicePlanAdminService>()
                .SaveAsync(null, new ServicePlanSaveRequest(
                    key, productId, $"پلن {key}", $"Plan {key}", null, null,
                    FiftyGibibytes, 30, 2, 1_000_000, "IRR",
                    true, true, null, 100, false, null));

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

    private async Task<Guid> ProvisionedServiceAsync(string prefix)
    {
        var userId = await _factory.CreateMemberAsync($"{prefix}-owner");
        var productId = await _factory.CreateProductAsync($"{prefix}-product");
        await CreateServerAsync($"{prefix}-server");
        var planId = await CreatePlanAsync(productId, $"{prefix}-plan");

        var serviceId = await _factory.WithScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<ICustomerServiceManager>()
                .CreateAsync(new CreateServiceRequest(userId, planId, null));

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

        await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IProvisioningExecutor>().RunPendingAsync(20));

        return serviceId;
    }

    private Task<CustomerService> LoadAsync(Guid serviceId) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .FirstAsync(service => service.Id == serviceId));

    // ----------------------------------------------------------------------- authorization ----

    [Fact]
    public async Task An_ordinary_member_cannot_reach_the_service_admin()
    {
        using var client = await ClientAsync("svc-admin-member");

        var response = await client.GetAsync("/Admin/CustomerServices");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_anonymous_visitor_is_sent_to_the_login_page()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/Admin/CustomerServices");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/Login",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Support_can_read_the_list_but_cannot_create_a_service()
    {
        using var client = await ClientAsync("svc-admin-support", RoleNames.Support);

        Assert.Equal(
            HttpStatusCode.OK, (await client.GetAsync("/Admin/CustomerServices")).StatusCode);

        var refused = await client.GetAsync("/Admin/CustomerServices/new");

        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            refused.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Support_cannot_end_a_service()
    {
        // The read-only role can watch a service; it must not be able to remove a customer's client
        // from a panel. Checked over HTTP because that is where the policy is actually enforced.
        var serviceId = await ProvisionedServiceAsync("svc-support-write");

        using var client = await ClientAsync("svc-admin-support-write", RoleNames.Support);

        var token = await client.GetAntiForgeryTokenAsync("/Admin/CustomerServices");

        var response = await client.PostAsync(
            $"/Admin/CustomerServices/{serviceId}/decommission",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        // And nothing happened.
        Assert.NotEqual(CustomerServiceStatus.Decommissioning, (await LoadAsync(serviceId)).Status);
    }

    [Fact]
    public async Task A_state_change_without_an_anti_forgery_token_is_refused()
    {
        var serviceId = await ProvisionedServiceAsync("svc-csrf");

        using var client = await ClientAsync("svc-admin-csrf", RoleNames.Admin);

        var response = await client.PostAsync(
            $"/Admin/CustomerServices/{serviceId}/suspend",
            new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(CustomerServiceStatus.Active, (await LoadAsync(serviceId)).Status);
    }

    // -------------------------------------------------------------------------- what leaks ----

    [Fact]
    public async Task Neither_the_delivery_token_nor_the_panel_credential_reaches_an_admin_page()
    {
        var serviceId = await ProvisionedServiceAsync("svc-leak");

        // The member's own link. An operator has no use for it and it is the member's credential.
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

        using var admin = await ClientAsync("svc-admin-leak", RoleNames.Admin);

        foreach (var path in new[] { "/Admin/CustomerServices", "/Admin/CustomerServices/new" })
        {
            var page = await admin.GetStringAsync(path);

            Assert.DoesNotContain(token!, page, StringComparison.Ordinal);
            Assert.DoesNotContain(PanelToken, page, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------------------ actions ----

    [Fact]
    public async Task An_administrator_creates_a_service_from_a_member_and_a_plan()
    {
        var userId = await _factory.CreateMemberAsync("svc-create-owner");
        var productId = await _factory.CreateProductAsync("svc-create-product");
        await CreateServerAsync("svc-create-server");
        var planId = await CreatePlanAsync(productId, "svc-create-plan");

        using var client = await ClientAsync("svc-admin-create", RoleNames.Admin);

        var token = await client.GetAntiForgeryTokenAsync("/Admin/CustomerServices/new");

        var response = await client.PostAsync("/Admin/CustomerServices/new", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", token),
            new("UserId", userId.ToString()),
            new("PlanId", planId.ToString()),
            new("Notes", "opened by the back office"),
        ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var created = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .FirstAsync(service => service.UserId == userId));

        // The terms came from the plan, not from anything the form could carry.
        Assert.Equal(FiftyGibibytes, created.TrafficBytes);
        Assert.Equal(2, created.DeviceLimit);
        Assert.NotNull(created.ServerId);
    }

    [Fact]
    public async Task A_form_that_tries_to_set_its_own_terms_is_ignored()
    {
        // Overposting is the attack this shape is built against: there is no bindable property for a
        // quota, an expiry, a server or an inbound, so extra fields land nowhere.
        var userId = await _factory.CreateMemberAsync("svc-overpost-owner");
        var productId = await _factory.CreateProductAsync("svc-overpost-product");
        var serverId = await CreateServerAsync("svc-overpost-server");
        var planId = await CreatePlanAsync(productId, "svc-overpost-plan");

        using var client = await ClientAsync("svc-admin-overpost", RoleNames.Admin);

        var token = await client.GetAntiForgeryTokenAsync("/Admin/CustomerServices/new");

        await client.PostAsync("/Admin/CustomerServices/new", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", token),
            new("UserId", userId.ToString()),
            new("PlanId", planId.ToString()),

            // All of these are invented field names an attacker would try.
            new("TrafficBytes", "999999999999"),
            new("DeviceLimit", "99"),
            new("ExpiresAt", "2099-01-01T00:00:00Z"),
            new("ServerId", serverId.ToString()),
            new("Status", "2"),
            new("PanelClientEmail", "attacker@example.com"),
        ]));

        var created = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .FirstAsync(service => service.UserId == userId));

        Assert.Equal(FiftyGibibytes, created.TrafficBytes);
        Assert.Equal(2, created.DeviceLimit);
        Assert.Equal(CustomerServiceStatus.Pending, created.Status);
        Assert.NotEqual("attacker@example.com", created.PanelClientEmail);
        Assert.True(created.ExpiresAt < DateTimeOffset.UtcNow.AddDays(31));
    }

    [Fact]
    public async Task Suspending_and_resuming_move_the_service_and_queue_the_panel_work()
    {
        var serviceId = await ProvisionedServiceAsync("svc-suspend");

        using var client = await ClientAsync("svc-admin-suspend", RoleNames.Admin);

        var token = await client.GetAntiForgeryTokenAsync("/Admin/CustomerServices");

        await client.PostAsync(
            $"/Admin/CustomerServices/{serviceId}/suspend",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        Assert.Equal(CustomerServiceStatus.Suspended, (await LoadAsync(serviceId)).Status);

        await client.PostAsync(
            $"/Admin/CustomerServices/{serviceId}/resume",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        Assert.Equal(CustomerServiceStatus.Active, (await LoadAsync(serviceId)).Status);
    }

    [Fact]
    public async Task Extending_adds_days_from_the_current_expiry()
    {
        var serviceId = await ProvisionedServiceAsync("svc-renew");
        var before = (await LoadAsync(serviceId)).ExpiresAt;

        Assert.NotNull(before);

        using var client = await ClientAsync("svc-admin-renew", RoleNames.Admin);

        var token = await client.GetAntiForgeryTokenAsync("/Admin/CustomerServices");

        await client.PostAsync(
            $"/Admin/CustomerServices/{serviceId}/renew",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("AdditionalDays", "15"),
            ]));

        var after = (await LoadAsync(serviceId)).ExpiresAt;

        Assert.NotNull(after);
        Assert.Equal(before!.Value.AddDays(15), after!.Value, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task An_absurd_extension_is_refused_rather_than_clamped()
    {
        var serviceId = await ProvisionedServiceAsync("svc-renew-absurd");
        var before = (await LoadAsync(serviceId)).ExpiresAt;

        using var client = await ClientAsync("svc-admin-renew-absurd", RoleNames.Admin);

        var token = await client.GetAntiForgeryTokenAsync("/Admin/CustomerServices");

        await client.PostAsync(
            $"/Admin/CustomerServices/{serviceId}/renew",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("AdditionalDays", "100000"),
            ]));

        Assert.Equal(before, (await LoadAsync(serviceId)).ExpiresAt);
    }

    [Fact]
    public async Task Ending_a_service_revokes_the_link_before_the_panel_is_told()
    {
        var serviceId = await ProvisionedServiceAsync("svc-decommission");

        using var client = await ClientAsync("svc-admin-decommission", RoleNames.Admin);

        var token = await client.GetAntiForgeryTokenAsync("/Admin/CustomerServices");

        await client.PostAsync(
            $"/Admin/CustomerServices/{serviceId}/decommission",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        var service = await LoadAsync(serviceId);

        Assert.Equal(CustomerServiceStatus.Decommissioning, service.Status);

        // Both forms of the token go at once. Leaving the sealed copy would let the member's page
        // keep showing a link that no longer resolves.
        Assert.Null(service.DeliveryTokenHash);
        Assert.Null(service.DeliveryTokenSealed);
    }

    [Fact]
    public async Task A_service_that_does_not_exist_reports_rather_than_crashes()
    {
        using var client = await ClientAsync("svc-admin-missing", RoleNames.Admin);

        var token = await client.GetAntiForgeryTokenAsync("/Admin/CustomerServices");

        var response = await client.PostAsync(
            $"/Admin/CustomerServices/{Guid.NewGuid()}/suspend",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
