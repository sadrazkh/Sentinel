using System.Net;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Products;
using Sentinel.IntegrationTests.Infrastructure;

namespace Sentinel.IntegrationTests;

/// <summary>
/// The product library over its real HTTP surface. What matters is what the running application
/// puts on the page and what it refuses — not that a rule function returns the right value,
/// which the unit suite already covers exhaustively.
/// </summary>
public sealed class ProductLibraryTests : IClassFixture<SentinelWebApplicationFactory>
{
    private readonly SentinelWebApplicationFactory _factory;

    public ProductLibraryTests(SentinelWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> SignedInAsync(string userName)
    {
        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    /// <summary>
    /// The portal's default culture is Persian, so asserting on an English name needs the
    /// culture asked for explicitly rather than assumed.
    /// </summary>
    private static async Task<string> GetEnglishAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Accept-Language", "en-US");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    // -------------------------------------------------------------------------- access ----

    [Theory]
    [InlineData("/products")]
    [InlineData("/products/library")]
    [InlineData("/products/anything")]
    public async Task The_library_is_closed_to_anonymous_visitors(string path)
    {
        using var client = _factory.CreateNonRedirectingClient();
        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/Login",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_member_sees_only_their_own_access_on_a_shared_product()
    {
        var holder = await _factory.CreateMemberAsync("lib-holder");
        await _factory.CreateMemberAsync("lib-outsider");

        var productId = await _factory.CreateProductAsync(
            "lib-restricted", requiresExplicitEntitlement: true);

        await _factory.GrantAsync(holder, productId);

        using var holderClient = await SignedInAsync("lib-holder");
        var holderPage = await holderClient.GetStringAsync("/products");

        using var outsiderClient = await SignedInAsync("lib-outsider");
        var outsiderPage = await outsiderClient.GetStringAsync("/products");

        // Both see it listed; only the holder is offered a way in.
        Assert.Contains("lib-restricted", holderPage, StringComparison.Ordinal);
        Assert.Contains("lib-restricted", outsiderPage, StringComparison.Ordinal);
        Assert.Contains("/apps/lib-restricted/open", holderPage, StringComparison.Ordinal);
        Assert.DoesNotContain("/apps/lib-restricted/open", outsiderPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_library_never_ships_the_destination_url_to_the_browser()
    {
        await _factory.CreateMemberAsync("lib-url");
        await _factory.CreateProductAsync("lib-url-product");

        using var client = await SignedInAsync("lib-url");
        var page = await client.GetStringAsync("/products");

        Assert.Contains("/apps/lib-url-product/open", page, StringComparison.Ordinal);
        Assert.DoesNotContain("apps.example.com", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_product_without_the_launchable_capability_is_never_offered_a_launch_link()
    {
        // The launch URL is present in the row; only the capability is missing. If the card
        // linked to it anyway, capabilities would be decoration rather than the mechanism.
        await _factory.CreateMemberAsync("lib-nocap");
        await _factory.CreateProductAsync("lib-nocap-product", capabilities: ProductCapability.None);

        using var client = await SignedInAsync("lib-nocap");
        var page = await client.GetStringAsync("/products");

        Assert.Contains("lib-nocap-product", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/apps/lib-nocap-product/open", page, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------- visibility ----

    [Theory]
    [InlineData(ProductReleaseStatus.Draft)]
    [InlineData(ProductReleaseStatus.Archived)]
    public async Task An_internal_release_stage_is_absent_from_the_library(ProductReleaseStatus status)
    {
        var userId = await _factory.CreateMemberAsync($"lib-hidden-{status}");
        var productId = await _factory.CreateProductAsync(
            $"lib-hidden-{status}-product", releaseStatus: status);

        // Granted as well, to prove the grant does not make an internal stage visible.
        await _factory.GrantAsync(userId, productId);

        using var client = await SignedInAsync($"lib-hidden-{status}");
        var page = await client.GetStringAsync("/products");

        Assert.DoesNotContain($"lib-hidden-{status}-product", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invitation_only_stage_is_hidden_from_an_uninvited_member()
    {
        await _factory.CreateMemberAsync("lib-uninvited");
        await _factory.CreateProductAsync(
            "lib-preview-product", releaseStatus: ProductReleaseStatus.PrivatePreview);

        using var client = await SignedInAsync("lib-uninvited");
        var page = await client.GetStringAsync("/products");

        Assert.DoesNotContain("lib-preview-product", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invitation_only_stage_is_visible_to_an_invited_member()
    {
        var userId = await _factory.CreateMemberAsync("lib-invited");
        var productId = await _factory.CreateProductAsync(
            "lib-invited-product",
            releaseStatus: ProductReleaseStatus.PrivatePreview,
            requiresExplicitEntitlement: true);

        await _factory.GrantAsync(userId, productId, source: EntitlementSource.BetaInvite);

        using var client = await SignedInAsync("lib-invited");
        var page = await client.GetStringAsync("/products");

        Assert.Contains("lib-invited-product", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_product_is_absent_even_for_a_member_holding_a_grant()
    {
        var userId = await _factory.CreateMemberAsync("lib-disabled");
        var productId = await _factory.CreateProductAsync("lib-disabled-product", isEnabled: false);

        await _factory.GrantAsync(userId, productId);

        using var client = await SignedInAsync("lib-disabled");
        var page = await client.GetStringAsync("/products");

        Assert.DoesNotContain("lib-disabled-product", page, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------- my library ----

    [Fact]
    public async Task My_library_holds_what_the_member_can_use_and_omits_what_they_cannot()
    {
        var userId = await _factory.CreateMemberAsync("lib-mine");

        var held = await _factory.CreateProductAsync(
            "lib-mine-held", requiresExplicitEntitlement: true);
        await _factory.CreateProductAsync("lib-mine-offered", requiresExplicitEntitlement: true);

        await _factory.GrantAsync(userId, held);

        using var client = await SignedInAsync("lib-mine");

        var mine = await client.GetStringAsync("/products/library");
        var discover = await client.GetStringAsync("/products");

        Assert.Contains("lib-mine-held", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("lib-mine-offered", mine, StringComparison.Ordinal);

        // Discover still shows both — narrowing "mine" must not narrow what exists.
        Assert.Contains("lib-mine-held", discover, StringComparison.Ordinal);
        Assert.Contains("lib-mine-offered", discover, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Something_the_member_held_and_let_lapse_stays_in_their_library()
    {
        // Removing it on expiry would hide the one thing they need to see to renew.
        var userId = await _factory.CreateMemberAsync(
            "lib-lapsed", membershipEndsAt: DateTimeOffset.UtcNow.AddDays(-90));

        var productId = await _factory.CreateProductAsync(
            "lib-lapsed-product", requiresExplicitEntitlement: true);

        await _factory.GrantAsync(
            userId,
            productId,
            startsAt: DateTimeOffset.UtcNow.AddDays(-60),
            expiresAt: DateTimeOffset.UtcNow.AddDays(-10));

        using var client = await SignedInAsync("lib-lapsed");
        var page = await client.GetStringAsync("/products/library");

        Assert.Contains("lib-lapsed-product", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/apps/lib-lapsed-product/open", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------ filters ----

    [Fact]
    public async Task A_search_narrows_the_list_without_widening_it()
    {
        var userId = await _factory.CreateMemberAsync("lib-search");

        await _factory.CreateProductAsync("lib-search-visible");

        // Hidden from this member. A search must not be a way of surfacing it.
        var hidden = await _factory.CreateProductAsync(
            "lib-search-hidden", releaseStatus: ProductReleaseStatus.Draft);

        await _factory.GrantAsync(userId, hidden);

        using var client = await SignedInAsync("lib-search");

        var matched = await client.GetStringAsync("/products?search=lib-search-visible");
        var probed = await client.GetStringAsync("/products?search=lib-search-hidden");

        // Asserted on the card's own link rather than the bare key: the search term is echoed
        // back into the input box, so a substring check would pass on the echo alone.
        Assert.Contains("/products/lib-search-visible", matched, StringComparison.Ordinal);
        Assert.DoesNotContain("/products/lib-search-hidden", probed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_category_filter_keeps_only_that_category()
    {
        var categoryId = await _factory.CreateProductCategoryAsync("lib-cat-a");
        await _factory.CreateProductCategoryAsync("lib-cat-b");

        await _factory.CreateMemberAsync("lib-category");
        await _factory.CreateProductAsync("lib-cat-inside", categoryId: categoryId);
        await _factory.CreateProductAsync("lib-cat-outside");

        using var client = await SignedInAsync("lib-category");
        var page = await client.GetStringAsync("/products?category=lib-cat-a");

        Assert.Contains("lib-cat-inside", page, StringComparison.Ordinal);
        Assert.DoesNotContain("lib-cat-outside", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_category_chip_counts_only_what_selecting_it_would_show()
    {
        // On My Library the chip must not advertise products that page excludes, or clicking
        // it lands on an empty list the count promised was full.
        var categoryId = await _factory.CreateProductCategoryAsync("lib-count");

        var userId = await _factory.CreateMemberAsync("lib-count-member");

        var held = await _factory.CreateProductAsync(
            "lib-count-held", categoryId: categoryId, requiresExplicitEntitlement: true);

        await _factory.CreateProductAsync(
            "lib-count-offered", categoryId: categoryId, requiresExplicitEntitlement: true);

        await _factory.GrantAsync(userId, held);

        using var client = await SignedInAsync("lib-count-member");

        var discover = await GetEnglishAsync(client, "/products");
        var mine = await GetEnglishAsync(client, "/products/library");

        Assert.Contains("Category lib-count (2)", discover, StringComparison.Ordinal);
        Assert.Contains("Category lib-count (1)", mine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_over_long_search_term_is_ignored_rather_than_erroring()
    {
        await _factory.CreateMemberAsync("lib-longsearch");
        await _factory.CreateProductAsync("lib-longsearch-product");

        using var client = await SignedInAsync("lib-longsearch");
        var response = await client.GetAsync($"/products?search={new string('x', 500)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("lib-longsearch-product", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------ details ----

    [Fact]
    public async Task The_details_page_shows_a_product_the_member_may_see()
    {
        await _factory.CreateMemberAsync("lib-detail");
        await _factory.CreateProductAsync("lib-detail-product", summaryEn: "A summary line.");

        using var client = await SignedInAsync("lib-detail");
        var page = await GetEnglishAsync(client, "/products/lib-detail-product");

        Assert.Contains("Product lib-detail-product", page, StringComparison.Ordinal);
        Assert.Contains("A summary line.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_details_page_of_a_hidden_product_is_indistinguishable_from_one_that_does_not_exist()
    {
        await _factory.CreateMemberAsync("lib-probe");
        await _factory.CreateProductAsync(
            "lib-probe-draft", releaseStatus: ProductReleaseStatus.Draft);

        using var client = await SignedInAsync("lib-probe");

        var draft = await client.GetAsync("/products/lib-probe-draft");
        var absent = await client.GetAsync("/products/lib-probe-nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, draft.StatusCode);
        Assert.Equal(absent.StatusCode, draft.StatusCode);
    }

    [Fact]
    public async Task The_details_page_of_a_locked_product_explains_the_lock_without_offering_a_way_in()
    {
        await _factory.CreateMemberAsync("lib-locked");
        await _factory.CreateProductAsync("lib-locked-product", requiresExplicitEntitlement: true);

        using var client = await SignedInAsync("lib-locked");
        var page = await GetEnglishAsync(client, "/products/lib-locked-product");

        Assert.Contains("Product lib-locked-product", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/apps/lib-locked-product/open", page, StringComparison.Ordinal);
        Assert.DoesNotContain("apps.example.com", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_details_page_names_where_a_members_access_came_from()
    {
        var userId = await _factory.CreateMemberAsync("lib-source");
        var productId = await _factory.CreateProductAsync(
            "lib-source-product", requiresExplicitEntitlement: true);

        await _factory.GrantAsync(userId, productId, source: EntitlementSource.Trial);

        using var client = await SignedInAsync("lib-source");
        var page = await GetEnglishAsync(client, "/products/lib-source-product");

        Assert.Contains("Trial", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_details_url_is_matched_case_insensitively_by_key()
    {
        await _factory.CreateMemberAsync("lib-case");
        await _factory.CreateProductAsync("lib-case-product");

        using var client = await SignedInAsync("lib-case");
        var response = await client.GetAsync("/products/LIB-CASE-PRODUCT");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------- tier gating ----

    [Fact]
    public async Task A_product_above_the_members_tier_is_listed_but_locked()
    {
        await _factory.CreateMemberAsync("lib-tier", tier: MembershipTier.Basic);
        await _factory.CreateProductAsync("lib-tier-product", minimumTier: MembershipTier.Elite);

        using var client = await SignedInAsync("lib-tier");
        var page = await client.GetStringAsync("/products");

        Assert.Contains("lib-tier-product", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/apps/lib-tier-product/open", page, StringComparison.Ordinal);
    }
}
