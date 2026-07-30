using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Products;
using Sentinel.IntegrationTests.Infrastructure;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Plans;

namespace Sentinel.IntegrationTests;

/// <summary>
/// Service plans over the real HTTP surface. Two things matter: a plan withheld from somebody is
/// indistinguishable from one that does not exist, and nothing a member sends can change what a
/// plan costs or includes.
/// </summary>
public sealed class ServicePlanTests : IClassFixture<SentinelWebApplicationFactory>
{
    private const long FiftyGibibytes = 53_687_091_200L;

    private readonly SentinelWebApplicationFactory _factory;

    public ServicePlanTests(SentinelWebApplicationFactory factory) => _factory = factory;

    private async Task<HttpClient> SignedInAsync(string userName)
    {
        var client = _factory.CreateNonRedirectingClient();
        await client.SignInAsync(userName, PortalTestData.MemberPassword);
        return client;
    }

    private Task<Guid> CreatePlanAsync(
        Guid productId,
        string key,
        bool visible = true,
        bool purchasable = false,
        string? country = null,
        long price = 2_500_000) =>
        _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IServicePlanAdminService>();

            var result = await admin.SaveAsync(null, new ServicePlanSaveRequest(
                key, productId, $"پلن {key}", $"Plan {key}", null, null,
                FiftyGibibytes, DurationDays: 30, DeviceLimit: 2,
                PriceMinorUnits: price, Currency: "IRR",
                IsVisible: visible, IsPurchasable: purchasable,
                CountryCode: country, DisplayOrder: 100, IsFeatured: false,
                ConcurrencyToken: null));

            Assert.True(result.Succeeded, result.ErrorKey);

            return result.Value;
        });

    private Task AddRuleAsync(Guid planId, AudienceRuleSaveRequest request) =>
        _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IServicePlanAdminService>();
            var result = await admin.AddRuleAsync(planId, request);

            Assert.True(result.Succeeded, result.ErrorKey);
            return true;
        });

    private Task<ServicePlanCatalog> CatalogFor(Guid userId, Guid productId) =>
        _factory.WithScopeAsync(services =>
            services.GetRequiredService<IServicePlanCatalog>().GetForMemberAsync(userId, productId));

    // ------------------------------------------------------------------------- audience ----

    [Fact]
    public async Task A_plan_with_no_rules_is_offered_to_any_member()
    {
        var userId = await _factory.CreateMemberAsync("plan-open");
        var productId = await _factory.CreateProductAsync("plan-open-product");
        await CreatePlanAsync(productId, "plan-open-1");

        var catalog = await CatalogFor(userId, productId);

        Assert.Single(catalog.Plans);
    }

    [Fact]
    public async Task A_hidden_plan_is_offered_to_nobody()
    {
        var userId = await _factory.CreateMemberAsync("plan-hidden");
        var productId = await _factory.CreateProductAsync("plan-hidden-product");
        await CreatePlanAsync(productId, "plan-hidden-1", visible: false);

        var catalog = await CatalogFor(userId, productId);

        Assert.Empty(catalog.Plans);
    }

    [Fact]
    public async Task A_deny_rule_withholds_a_plan_from_one_member_only()
    {
        var excluded = await _factory.CreateMemberAsync("plan-excluded");
        var included = await _factory.CreateMemberAsync("plan-included");
        var productId = await _factory.CreateProductAsync("plan-deny-product");

        var planId = await CreatePlanAsync(productId, "plan-deny-1");

        await AddRuleAsync(planId, new AudienceRuleSaveRequest(
            AudienceEffect.Allow, AudienceRuleKind.Everyone, null, null, null, null));

        await AddRuleAsync(planId, new AudienceRuleSaveRequest(
            AudienceEffect.Deny, AudienceRuleKind.User, null, null, excluded, "test exclusion"));

        Assert.Empty((await CatalogFor(excluded, productId)).Plans);
        Assert.Single((await CatalogFor(included, productId)).Plans);
    }

    [Fact]
    public async Task A_tier_restricted_plan_is_withheld_from_a_lower_tier()
    {
        var basic = await _factory.CreateMemberAsync("plan-basic", tier: MembershipTier.Basic);
        var elite = await _factory.CreateMemberAsync("plan-elite", tier: MembershipTier.Elite);
        var productId = await _factory.CreateProductAsync("plan-tier-product");

        var planId = await CreatePlanAsync(productId, "plan-tier-1");

        await AddRuleAsync(planId, new AudienceRuleSaveRequest(
            AudienceEffect.Allow, AudienceRuleKind.MinimumTier, MembershipTier.Elite, null, null, null));

        Assert.Empty((await CatalogFor(basic, productId)).Plans);
        Assert.Single((await CatalogFor(elite, productId)).Plans);
    }

    [Fact]
    public async Task A_lapsed_membership_does_not_satisfy_a_tier_rule()
    {
        // The tier comes from the resolved snapshot, not the raw row. An expired Elite membership
        // must not keep opening an Elite-only plan.
        var lapsed = await _factory.CreateMemberAsync(
            "plan-lapsed",
            tier: MembershipTier.Elite,
            // Well past any grace period, so the resolver reports it as granting nothing.
            membershipEndsAt: DateTimeOffset.UtcNow.AddDays(-90));

        var productId = await _factory.CreateProductAsync("plan-lapsed-product");
        var planId = await CreatePlanAsync(productId, "plan-lapsed-1");

        await AddRuleAsync(planId, new AudienceRuleSaveRequest(
            AudienceEffect.Allow, AudienceRuleKind.MinimumTier, MembershipTier.Elite, null, null, null));

        Assert.Empty((await CatalogFor(lapsed, productId)).Plans);
    }

    [Fact]
    public async Task A_role_rule_matches_a_members_role()
    {
        var supporter = await _factory.CreateMemberAsync("plan-support");
        await _factory.AddToRoleAsync("plan-support", RoleNames.Support);

        var ordinary = await _factory.CreateMemberAsync("plan-ordinary");
        var productId = await _factory.CreateProductAsync("plan-role-product");

        var planId = await CreatePlanAsync(productId, "plan-role-1");

        await AddRuleAsync(planId, new AudienceRuleSaveRequest(
            AudienceEffect.Allow, AudienceRuleKind.Role, null, RoleNames.Support, null, null));

        Assert.Single((await CatalogFor(supporter, productId)).Plans);
        Assert.Empty((await CatalogFor(ordinary, productId)).Plans);
    }

    // ------------------------------------------------------------------------ purchasing ----

    [Fact]
    public async Task No_plan_can_be_ordered_while_the_purchase_feature_is_off()
    {
        // The feature ships off, so this is the shipped behaviour: a price list, not a shop.
        var userId = await _factory.CreateMemberAsync("plan-noorder");
        var productId = await _factory.CreateProductAsync("plan-noorder-product");

        await CreatePlanAsync(productId, "plan-noorder-1", purchasable: true);

        var catalog = await CatalogFor(userId, productId);

        Assert.False(catalog.PurchasingEnabled);
        Assert.All(catalog.Plans, plan => Assert.False(plan.CanOrder));
    }

    [Fact]
    public async Task The_product_page_never_renders_an_order_button()
    {
        var userId = await _factory.CreateMemberAsync("plan-page-noorder");
        var productId = await _factory.CreateProductAsync(
            "plan-page-noorder-product", capabilities: ProductCapability.HasConfigurations);

        await _factory.GrantAsync(userId, productId);
        await CreatePlanAsync(productId, "plan-page-noorder-1", purchasable: true);

        using var client = await SignedInAsync("plan-page-noorder");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/vpn/plan-page-noorder-product");
        request.Headers.Add("Accept-Language", "en-US");

        using var response = await client.SendAsync(request);
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The read-only notice is shown instead, which says plainly what the state is.
        Assert.Contains("Ordering through the portal is not open yet", page, StringComparison.Ordinal);
        Assert.DoesNotContain(">Order<", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------ the page ----

    [Fact]
    public async Task The_vpn_page_is_not_reachable_for_a_product_the_member_cannot_see()
    {
        // Draft is an internal state that ProductAccessRules hides outright — as opposed to a
        // locked product, which stays visible so somebody can see what obtaining it would give
        // them. Absent and hidden answer identically, so the URL cannot enumerate what is being
        // worked on.
        await _factory.CreateMemberAsync("vpn-page-denied");
        await _factory.CreateProductAsync(
            "vpn-page-denied-product", releaseStatus: ProductReleaseStatus.Draft);

        using var client = await SignedInAsync("vpn-page-denied");

        var hidden = await client.GetAsync("/vpn/vpn-page-denied-product");
        var absent = await client.GetAsync("/vpn/no-such-product-at-all");

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(absent.StatusCode, hidden.StatusCode);
    }

    [Fact]
    public async Task A_member_without_the_service_yet_still_sees_the_plans()
    {
        // The pre-purchase audience. A locked product stays visible precisely so somebody deciding
        // whether to buy can read what it costs — withholding the price list from them would defeat
        // the point of having one.
        var userId = await _factory.CreateMemberAsync("vpn-prepurchase");
        var productId = await _factory.CreateProductAsync(
            "vpn-prepurchase-product", requiresExplicitEntitlement: true);

        await CreatePlanAsync(productId, "vpn-prepurchase-plan");

        using var client = await SignedInAsync("vpn-prepurchase");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/vpn/vpn-prepurchase-product");
        request.Headers.Add("Accept-Language", "en-US");

        using var response = await client.SendAsync(request);
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Plan vpn-prepurchase-plan", page, StringComparison.Ordinal);

        // But no services and no configurations, because they hold none.
        Assert.DoesNotContain("My services", page, StringComparison.Ordinal);

        Assert.NotEqual(Guid.Empty, userId);
    }

    [Fact]
    public async Task The_vpn_page_is_closed_to_anonymous_visitors()
    {
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.GetAsync("/vpn/anything");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/Login",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unrecognised_tab_is_a_wrong_url_rather_than_a_silent_fallback()
    {
        var userId = await _factory.CreateMemberAsync("vpn-badtab");
        var productId = await _factory.CreateProductAsync("vpn-badtab-product");
        await _factory.GrantAsync(userId, productId);

        using var client = await SignedInAsync("vpn-badtab");

        var response = await client.GetAsync("/vpn/vpn-badtab-product/not-a-tab");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_tab_redirects_to_the_overview_rather_than_showing_nothing()
    {
        // The link was legitimate; it simply has no content for this member. A 404 would be wrong,
        // and an empty panel would be worse.
        var userId = await _factory.CreateMemberAsync("vpn-emptytab");
        var productId = await _factory.CreateProductAsync("vpn-emptytab-product");
        await _factory.GrantAsync(userId, productId);

        using var client = await SignedInAsync("vpn-emptytab");

        var response = await client.GetAsync("/vpn/vpn-emptytab-product/downloads");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/vpn/vpn-emptytab-product",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_page_renders_in_both_languages_without_a_raw_key()
    {
        var userId = await _factory.CreateMemberAsync("vpn-langs");
        var productId = await _factory.CreateProductAsync("vpn-langs-product");

        await _factory.GrantAsync(userId, productId);
        await CreatePlanAsync(productId, "vpn-langs-plan", country: "DE");

        using var client = await SignedInAsync("vpn-langs");

        foreach (var culture in new[] { "fa-IR", "en-US" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/vpn/vpn-langs-product");
            request.Headers.Add("Accept-Language", culture);

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var page = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain("plan.", page, StringComparison.Ordinal);
            Assert.DoesNotContain("vpnTab.", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_plan_with_no_ready_server_says_so_rather_than_offering_a_location()
    {
        // No servers exist in this fixture, so nothing could be delivered. Saying so is better than
        // listing a country the portal cannot currently provision.
        var userId = await _factory.CreateMemberAsync("plan-nocapacity");
        var productId = await _factory.CreateProductAsync("plan-nocapacity-product");

        await CreatePlanAsync(productId, "plan-nocapacity-1", country: "DE");

        var catalog = await CatalogFor(userId, productId);

        Assert.Single(catalog.Plans);
        Assert.Empty(catalog.AvailableCountries);
    }

    // ------------------------------------------------------------------------ validation ----

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    [InlineData(-1)]
    public async Task An_impossible_duration_is_refused(int days) =>
        await _factory.WithScopeAsync(async services =>
        {
            var productId = await _factory.CreateProductAsync($"plan-duration-{days}");
            var admin = services.GetRequiredService<IServicePlanAdminService>();

            var result = await admin.SaveAsync(null, new ServicePlanSaveRequest(
                $"plan-duration-{Math.Abs(days)}", productId, "پلن", "Plan", null, null,
                FiftyGibibytes, days, 2, 1000, "IRR", true, false, null, 100, false, null));

            Assert.False(result.Succeeded);
            Assert.Equal(PlanErrors.DurationInvalid, result.ErrorKey);
        });

    [Fact]
    public async Task A_negative_price_or_quota_is_refused()
    {
        var productId = await _factory.CreateProductAsync("plan-negative-product");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IServicePlanAdminService>();

            foreach (var (traffic, price, devices) in new[]
                     {
                         (-1L, 1000L, 2),
                         (FiftyGibibytes, -1L, 2),
                         (FiftyGibibytes, 1000L, -1),
                     })
            {
                var result = await admin.SaveAsync(null, new ServicePlanSaveRequest(
                    "plan-negative", productId, "پلن", "Plan", null, null,
                    traffic, 30, devices, price, "IRR", true, false, null, 100, false, null));

                Assert.False(result.Succeeded);
                Assert.Equal(PlanErrors.NegativeAmount, result.ErrorKey);
            }
        });
    }

    [Fact]
    public async Task A_plan_on_a_product_that_does_not_exist_is_refused() =>
        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IServicePlanAdminService>();

            var result = await admin.SaveAsync(null, new ServicePlanSaveRequest(
                "plan-orphan", Guid.NewGuid(), "پلن", "Plan", null, null,
                FiftyGibibytes, 30, 2, 1000, "IRR", true, false, null, 100, false, null));

            Assert.False(result.Succeeded);
            Assert.Equal(PlanErrors.ProductNotFound, result.ErrorKey);
        });

    [Fact]
    public async Task A_half_filled_rule_is_refused_rather_than_stored_as_a_no_op()
    {
        // A rule that matches nothing would sit in the operator's list looking like a restriction
        // while doing nothing at all.
        var productId = await _factory.CreateProductAsync("plan-badrule-product");
        var planId = await CreatePlanAsync(productId, "plan-badrule-1");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IServicePlanAdminService>();

            foreach (var request in new[]
                     {
                         new AudienceRuleSaveRequest(
                             AudienceEffect.Allow, AudienceRuleKind.MembershipTier, null, null, null, null),
                         new AudienceRuleSaveRequest(
                             AudienceEffect.Allow, AudienceRuleKind.Role, null, "  ", null, null),
                         new AudienceRuleSaveRequest(
                             AudienceEffect.Deny, AudienceRuleKind.User, null, null, Guid.Empty, null),
                     })
            {
                var result = await admin.AddRuleAsync(planId, request);

                Assert.False(result.Succeeded, $"{request.Kind} should have been refused.");
                Assert.Equal(PlanErrors.RuleIncomplete, result.ErrorKey);
            }
        });
    }

    [Fact]
    public async Task Only_one_plan_per_product_stays_featured()
    {
        var productId = await _factory.CreateProductAsync("plan-featured-product");

        await _factory.WithScopeAsync(async services =>
        {
            var admin = services.GetRequiredService<IServicePlanAdminService>();
            var query = services.GetRequiredService<IServicePlanAdminQuery>();

            foreach (var key in new[] { "plan-feat-a", "plan-feat-b" })
            {
                var result = await admin.SaveAsync(null, new ServicePlanSaveRequest(
                    key, productId, "پلن", "Plan", null, null,
                    FiftyGibibytes, 30, 2, 1000, "IRR", true, false, null, 100,
                    IsFeatured: true, ConcurrencyToken: null));

                Assert.True(result.Succeeded, result.ErrorKey);
            }

            var plans = await query.ListAsync();
            var featured = plans.Where(plan => plan.ProductId == productId && plan.IsFeatured).ToList();

            // Two "recommended" options recommend nothing.
            Assert.Single(featured);
            Assert.Equal("plan-feat-b", featured[0].Key);
        });
    }

    // ----------------------------------------------------------------------- admin pages ----

    [Fact]
    public async Task An_ordinary_member_cannot_reach_the_plan_admin()
    {
        await _factory.CreateMemberAsync("plan-admin-member");

        using var client = await SignedInAsync("plan-admin-member");

        var response = await client.GetAsync("/Admin/ServicePlans");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "/Account/AccessDenied",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_admin_pages_render_in_both_languages()
    {
        await _factory.CreateMemberAsync("plan-admin");
        await _factory.AddToRoleAsync("plan-admin", RoleNames.Admin);

        var productId = await _factory.CreateProductAsync("plan-adminpage-product");
        var planId = await CreatePlanAsync(productId, "plan-adminpage-1");

        using var client = await SignedInAsync("plan-admin");

        foreach (var culture in new[] { "fa-IR", "en-US" })
        {
            foreach (var path in new[]
                     {
                         "/Admin/ServicePlans",
                         "/Admin/ServicePlans/new",
                         $"/Admin/ServicePlans/{planId}",
                     })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Add("Accept-Language", culture);

                using var response = await client.SendAsync(request);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var page = await response.Content.ReadAsStringAsync();

                Assert.DoesNotContain("admin.plan.", page, StringComparison.Ordinal);
                Assert.DoesNotContain("audienceKind.", page, StringComparison.Ordinal);
            }
        }
    }
}
