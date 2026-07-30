using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Domain.Products;
using Sentinel.IntegrationTests.Infrastructure;
using Sentinel.Vpn.Delivery;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Plans;
using Sentinel.Vpn.Provisioning;
using Sentinel.Vpn.Servers;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The member's own view of a service the portal provisions.
/// <para>
/// The thing being proved is narrow and important: this page shows a bearer capability — the
/// subscription URL — and it must show it to exactly one person. So the tests are about who sees
/// which link, and about the page refusing to offer one that would not work.
/// </para>
/// </summary>
public sealed class MemberServicePageTests : IClassFixture<VpnTestFactory>, IAsyncLifetime
{
    private const long FiftyGibibytes = 53_687_091_200L;
    private const string PanelToken = "integration-only-panel-token-24680";

    private readonly VpnTestFactory _factory;

    public MemberServicePageTests(VpnTestFactory factory) => _factory = factory;

    public Task InitializeAsync()
    {
        _factory.Panel.AllCallsUnknown = false;
        _factory.Panel.ClearScripts();

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------------------- fixtures ----

    private async Task<HttpClient> SignedInAsync(string userName)
    {
        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);

        return client;
    }

    private Task CreateServerAsync(string key) =>
        _factory.WithScopeAsync(async services =>
        {
            var created = await services.GetRequiredService<IVpnServerAdminService>()
                .SaveAsync(null, new VpnServerSaveRequest(
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
        });

    /// <summary>A member who owns a live service on a visible product, and the token that reaches it.</summary>
    private async Task<(Guid UserId, Guid ServiceId, string ProductKey, string Token)> LiveServiceAsync(
        string prefix)
    {
        var userId = await _factory.CreateMemberAsync($"{prefix}-member");

        var productKey = $"{prefix}-product";
        var productId = await _factory.CreateProductAsync(
            productKey, capabilities: ProductCapability.HasConfigurations);

        await _factory.GrantAsync(userId, productId);
        await CreateServerAsync($"{prefix}-server");

        var planId = await _factory.WithScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<IServicePlanAdminService>()
                .SaveAsync(null, new ServicePlanSaveRequest(
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

        await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IProvisioningExecutor>().RunPendingAsync(20));

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

        return (userId, serviceId, productKey, token!);
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

    // ------------------------------------------------------------------------------- tests ----

    [Fact]
    public async Task The_services_tab_shows_the_members_own_link()
    {
        var (_, _, productKey, token) = await LiveServiceAsync("page-own");

        using var client = await SignedInAsync("page-own-member");

        var response = await client.GetAsync($"/vpn/{productKey}/services");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadAsStringAsync();

        Assert.Contains($"/s/{token}", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_shared_navigation_still_points_out_of_the_area()
    {
        // A tag helper with no asp-area inherits the ambient one, so on a page inside the Vpn area
        // every link in the shared layout silently became /Vpn/Dashboard, /Vpn/Profile and — worst
        // of the lot — a sign-out form posting to /Vpn/Account/Logout. Each of those is a 404, and
        // nothing outside this area would ever have shown it.
        var (_, _, productKey, _) = await LiveServiceAsync("page-nav");

        using var client = await SignedInAsync("page-nav-member");

        var page = await client.GetStringAsync($"/vpn/{productKey}/services");

        foreach (var expected in new[]
                 {
                     "\"/Dashboard\"", "\"/Apps\"", "\"/Membership\"",
                     "\"/Configs\"", "\"/Profile\"", "\"/Security\"",
                     "\"/Account/Logout\"",
                 })
        {
            Assert.Contains(expected, page, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("\"/vpn/Dashboard\"", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"/vpn/Account/Logout\"", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Another_member_never_sees_that_link()
    {
        // The list is scoped by owner in the query itself rather than filtered afterwards, which is
        // what makes this hold even if the second member is entitled to the same product.
        var (_, _, productKey, token) = await LiveServiceAsync("page-other");

        var strangerId = await _factory.CreateMemberAsync("page-other-stranger");

        var productId = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .Select(service => service.ProductId)
                .FirstAsync());

        Assert.NotEqual(Guid.Empty, productId);
        await _factory.GrantAsync(strangerId, productId);

        using var stranger = await SignedInAsync("page-other-stranger");

        var page = await stranger.GetStringAsync($"/vpn/{productKey}");

        Assert.DoesNotContain(token, page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lapsed_service_is_listed_but_its_link_is_not_offered()
    {
        // A link that would answer 410 is worse than none: the member pastes it into an application
        // that then reports a broken subscription, and support gets the call.
        var (_, serviceId, productKey, token) = await LiveServiceAsync("page-lapsed");

        await MutateAsync(serviceId, service =>
            service.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));

        using var client = await SignedInAsync("page-lapsed-member");

        var page = await client.GetStringAsync($"/vpn/{productKey}/services");

        Assert.DoesNotContain($"/s/{token}", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotating_from_the_page_replaces_the_link_that_is_shown()
    {
        var (_, _, productKey, original) = await LiveServiceAsync("page-rotate");

        using var client = await SignedInAsync("page-rotate-member");

        var serviceId = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .Where(service => service.DeliveryTokenHash == DeliveryToken.Hash(original))
                .Select(service => service.Id)
                .FirstAsync());

        var token = await client.GetAntiForgeryTokenAsync($"/vpn/{productKey}/services");

        var response = await client.PostAsync(
            $"/vpn/{productKey}/link/rotate",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("serviceId", serviceId.ToString()),
            ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var page = await client.GetStringAsync($"/vpn/{productKey}/services");

        // The old one is gone from the page, and a different one is there in its place.
        Assert.DoesNotContain($"/s/{original}", page, StringComparison.Ordinal);
        Assert.Contains("/s/", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_cannot_rotate_a_link_that_is_not_theirs()
    {
        var (_, serviceId, productKey, original) = await LiveServiceAsync("page-rotate-other");

        var strangerId = await _factory.CreateMemberAsync("page-rotate-other-stranger");

        var productId = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .Where(service => service.Id == serviceId)
                .Select(service => service.ProductId)
                .FirstAsync());

        await _factory.GrantAsync(strangerId, productId);

        using var stranger = await SignedInAsync("page-rotate-other-stranger");

        var token = await stranger.GetAntiForgeryTokenAsync($"/vpn/{productKey}");

        await stranger.PostAsync(
            $"/vpn/{productKey}/link/rotate",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("serviceId", serviceId.ToString()),
            ]));

        // The owner's link still works, because the rotation never happened.
        var hash = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .Where(service => service.Id == serviceId)
                .Select(service => service.DeliveryTokenHash)
                .FirstAsync());

        Assert.Equal(DeliveryToken.Hash(original), hash);
    }

    [Fact]
    public async Task Rotating_without_an_anti_forgery_token_is_refused()
    {
        var (_, serviceId, productKey, original) = await LiveServiceAsync("page-rotate-csrf");

        using var client = await SignedInAsync("page-rotate-csrf-member");

        var response = await client.PostAsync(
            $"/vpn/{productKey}/link/rotate",
            new FormUrlEncodedContent([new("serviceId", serviceId.ToString())]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var hash = await _factory.WithScopeAsync(services =>
            services.GetRequiredService<IVpnDbContext>()
                .CustomerServices.AsNoTracking()
                .Where(service => service.Id == serviceId)
                .Select(service => service.DeliveryTokenHash)
                .FirstAsync());

        Assert.Equal(DeliveryToken.Hash(original), hash);
    }

    [Fact]
    public async Task The_new_link_is_never_carried_in_the_redirect()
    {
        // A query string or fragment lands in browser history, in a proxy log and in the next
        // request's referrer. The page re-reads the sealed copy instead.
        var (_, serviceId, productKey, _) = await LiveServiceAsync("page-rotate-url");

        using var client = await SignedInAsync("page-rotate-url-member");

        var token = await client.GetAntiForgeryTokenAsync($"/vpn/{productKey}/services");

        var response = await client.PostAsync(
            $"/vpn/{productKey}/link/rotate",
            new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token),
                new("serviceId", serviceId.ToString()),
            ]));

        var location = response.Headers.Location?.ToString() ?? string.Empty;

        Assert.DoesNotContain("#", location, StringComparison.Ordinal);

        var fresh = await _factory.WithScopeAsync(async services =>
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

        Assert.NotNull(fresh);
        Assert.DoesNotContain(fresh!, location, StringComparison.Ordinal);
    }
}
