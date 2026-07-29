using System.Net;
using Microsoft.AspNetCore.Hosting;
using Sentinel.Domain.Products;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// A factory whose product library is switched off entirely.
/// </summary>
public sealed class LibraryDisabledFactory : SentinelWebApplicationFactory
{
    protected override void ConfigureTestSettings(IWebHostBuilder builder) =>
        builder.UseSetting("Features:ProductLibraryEnabled", "false");
}

/// <summary>
/// A factory that keeps My Library but withdraws the discovery page — the shape an operator
/// would use to run an invitation-only portal.
/// </summary>
public sealed class DiscoveryDisabledFactory : SentinelWebApplicationFactory
{
    protected override void ConfigureTestSettings(IWebHostBuilder builder) =>
        builder.UseSetting("Features:ProductDiscoveryEnabled", "false");
}

/// <summary>A factory with pre-release products withdrawn from the catalogue.</summary>
public sealed class BetaDisabledFactory : SentinelWebApplicationFactory
{
    protected override void ConfigureTestSettings(IWebHostBuilder builder) =>
        builder.UseSetting("Features:BetaProductsEnabled", "false");
}

/// <summary>
/// The claim a feature flag makes is that switching it off closes the endpoints, not merely
/// that it hides a link. These tests are what makes that claim true rather than intended.
/// </summary>
public sealed class FeatureFlagTests
{
    private static async Task<HttpClient> SignedInAsync(
        SentinelWebApplicationFactory factory,
        string userName)
    {
        var client = factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    [Theory]
    [InlineData("/products")]
    [InlineData("/products/library")]
    [InlineData("/products/some-key")]
    public async Task Switching_the_library_off_closes_every_one_of_its_endpoints(string path)
    {
        await using var factory = new LibraryDisabledFactory();

        await factory.CreateMemberAsync("flag-library");
        await factory.CreateProductAsync("some-key");

        using var client = await SignedInAsync(factory, "flag-library");
        var response = await client.GetAsync(path);

        // 404 rather than 403: a withdrawn feature should be indistinguishable from one that
        // was never built, so the response is not a map of the unreleased surface.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Switching_the_library_off_also_removes_its_navigation_link()
    {
        await using var factory = new LibraryDisabledFactory();
        await factory.CreateMemberAsync("flag-library-nav");

        using var client = await SignedInAsync(factory, "flag-library-nav");
        var page = await client.GetStringAsync("/dashboard");

        Assert.DoesNotContain("href=\"/products", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Withdrawing_discovery_closes_that_page_but_leaves_my_library_open()
    {
        await using var factory = new DiscoveryDisabledFactory();

        var userId = await factory.CreateMemberAsync("flag-discovery");
        var productId = await factory.CreateProductAsync(
            "flag-discovery-product", requiresExplicitEntitlement: true);

        await factory.GrantAsync(userId, productId);

        using var client = await SignedInAsync(factory, "flag-discovery");

        var discover = await client.GetAsync("/products");
        var mine = await client.GetAsync("/products/library");

        Assert.Equal(HttpStatusCode.NotFound, discover.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);

        Assert.Contains(
            "flag-discovery-product",
            await mine.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Withdrawing_beta_products_hides_them_from_a_member_without_an_invitation()
    {
        await using var factory = new BetaDisabledFactory();

        await factory.CreateMemberAsync("flag-beta");
        await factory.CreateProductAsync(
            "flag-beta-product", releaseStatus: ProductReleaseStatus.Beta);

        using var client = await SignedInAsync(factory, "flag-beta");

        var list = await client.GetStringAsync("/products");
        var details = await client.GetAsync("/products/flag-beta-product");

        Assert.DoesNotContain("flag-beta-product", list, StringComparison.Ordinal);

        // The details page must agree with the list, or the flag only hides the card.
        Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
    }

    [Fact]
    public async Task Withdrawing_beta_products_does_not_revoke_an_invitation_already_issued()
    {
        // A flag governs what is offered, not what somebody was already promised.
        await using var factory = new BetaDisabledFactory();

        var userId = await factory.CreateMemberAsync("flag-beta-invited");
        var productId = await factory.CreateProductAsync(
            "flag-beta-invited-product",
            releaseStatus: ProductReleaseStatus.Beta,
            requiresExplicitEntitlement: true);

        await factory.GrantAsync(userId, productId);

        using var client = await SignedInAsync(factory, "flag-beta-invited");
        var page = await client.GetStringAsync("/products");

        Assert.Contains("flag-beta-invited-product", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_financial_features_are_off_in_the_shipped_configuration()
    {
        // Not a behavioural test so much as a standing guard: if a default ever flips, money
        // handling would go live without anyone deciding that it should.
        await using var factory = new SentinelWebApplicationFactory();

        var gate = factory.Services.GetService(typeof(Sentinel.Application.Features.IFeatureGate))
            as Sentinel.Application.Features.IFeatureGate;

        Assert.NotNull(gate);
        Assert.False(gate!.Current.PurchasesEnabled);
        Assert.False(gate.Current.PaymentsEnabled);
        Assert.False(gate.Current.WalletEnabled);
    }

    [Fact]
    public async Task An_unknown_feature_name_reads_as_off()
    {
        // Failing closed means a renamed flag disables something rather than leaving it open.
        await using var factory = new SentinelWebApplicationFactory();

        var gate = (Sentinel.Application.Features.IFeatureGate)factory.Services
            .GetService(typeof(Sentinel.Application.Features.IFeatureGate))!;

        Assert.False(gate.IsEnabled("NoSuchFeatureEnabled"));

        await Task.CompletedTask;
    }
}
