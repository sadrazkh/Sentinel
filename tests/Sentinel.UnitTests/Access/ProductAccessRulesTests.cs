using Sentinel.Application.Access;
using Sentinel.Application.Features;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Products;

namespace Sentinel.UnitTests.Access;

/// <summary>
/// The presentation layer over the access decision: what the library labels a product and which
/// single button it leads with. The underlying security answer is decided by
/// <see cref="AccessRuleEvaluator"/> and is deliberately not re-derived here.
/// </summary>
public sealed class ProductAccessRulesTests
{
    private static ProductFacts Product(
        ProductCapability capabilities = ProductCapability.Launchable,
        ProductReleaseStatus release = ProductReleaseStatus.Stable,
        bool isEnabled = true,
        bool hasLaunchUrl = true,
        ProductType type = ProductType.WebApplication) =>
        new(Guid.NewGuid(), "demo", type, capabilities, release, isEnabled,
            RequiresExplicitEntitlement: false, hasLaunchUrl, MinimumTier: null);

    private static FeatureFlags Features(
        bool purchases = false,
        bool beta = true) =>
        new() { PurchasesEnabled = purchases, BetaProductsEnabled = beta };

    private static ProductAccessDecision Evaluate(
        ProductFacts product,
        bool allowed = true,
        AccessDenialReason reason = AccessDenialReason.None,
        GrantFacts? grant = null,
        FeatureFlags? features = null) =>
        ProductAccessRules.Evaluate(
            product,
            allowed ? AccessDecision.Allowed : AccessDecision.Denied(reason),
            grant,
            features ?? Features());

    // -------------------------------------------------------------------- invisibility ----

    [Theory]
    [InlineData(ProductReleaseStatus.Draft)]
    [InlineData(ProductReleaseStatus.Archived)]
    public void An_internal_release_stage_is_never_shown(ProductReleaseStatus release)
    {
        // Draft and Archived are operator states. Even a member holding a grant must not see
        // them, or the catalogue would leak what is being worked on.
        var decision = Evaluate(
            Product(release: release),
            grant: new GrantFacts(EntitlementSource.AdminGrant, IsUsable: true));

        Assert.False(decision.CanView);
        Assert.Equal(ProductPrimaryAction.None, decision.PrimaryAction);
    }

    [Fact]
    public void A_disabled_product_is_never_shown()
    {
        Assert.False(Evaluate(Product(isEnabled: false)).CanView);
    }

    [Theory]
    [InlineData(ProductReleaseStatus.PrivatePreview)]
    [InlineData(ProductReleaseStatus.Alpha)]
    public void An_invitation_only_stage_is_hidden_without_a_grant(ProductReleaseStatus release)
    {
        // Membership does not open a private preview; only an explicit invitation does.
        Assert.False(Evaluate(Product(release: release)).CanView);
    }

    [Theory]
    [InlineData(ProductReleaseStatus.PrivatePreview)]
    [InlineData(ProductReleaseStatus.Alpha)]
    public void An_invitation_only_stage_is_visible_to_an_invited_member(ProductReleaseStatus release)
    {
        var decision = Evaluate(
            Product(release: release),
            grant: new GrantFacts(EntitlementSource.BetaInvite, IsUsable: true));

        Assert.True(decision.CanView);
        Assert.Equal(ProductAccessStatus.BetaAccess, decision.Status);
    }

    [Fact]
    public void Switching_beta_products_off_hides_them_from_everyone_else()
    {
        var product = Product(release: ProductReleaseStatus.Beta);

        Assert.False(Evaluate(product, features: Features(beta: false)).CanView);
    }

    [Fact]
    public void Switching_beta_products_off_does_not_revoke_an_invitation_already_issued()
    {
        // A flag governs what is offered, not what somebody was already promised.
        var decision = Evaluate(
            Product(release: ProductReleaseStatus.Beta),
            grant: new GrantFacts(EntitlementSource.BetaInvite, IsUsable: true),
            features: Features(beta: false));

        Assert.True(decision.CanView);
    }

    // ------------------------------------------------------------------- capabilities ----

    [Fact]
    public void A_product_without_the_launchable_capability_is_never_openable()
    {
        // Access alone is not enough: a downloadable tool has nowhere to "open".
        var decision = Evaluate(Product(capabilities: ProductCapability.Downloadable));

        Assert.False(decision.CanLaunch);
        Assert.True(decision.CanDownload);
        Assert.Equal(ProductPrimaryAction.Download, decision.PrimaryAction);
    }

    [Fact]
    public void A_launchable_product_with_no_destination_is_not_openable()
    {
        // The capability says it should open; without a URL there is nothing to open, and
        // offering the button would produce a dead click.
        var decision = Evaluate(Product(hasLaunchUrl: false));

        Assert.False(decision.CanLaunch);
        Assert.NotEqual(ProductPrimaryAction.Open, decision.PrimaryAction);
    }

    [Fact]
    public void Capabilities_alone_never_grant_access()
    {
        // Every permission is the conjunction of a capability and the underlying decision.
        var everything = (ProductCapability)~0;

        var decision = Evaluate(
            Product(capabilities: everything),
            allowed: false,
            reason: AccessDenialReason.MembershipInvalid);

        Assert.False(decision.CanLaunch);
        Assert.False(decision.CanDownload);
        Assert.False(decision.CanManageService);
        Assert.False(decision.CanViewPrivateDocs);
    }

    // ------------------------------------------------------------------------ status ----

    [Theory]
    [InlineData(EntitlementSource.Purchase, ProductAccessStatus.Owned)]
    [InlineData(EntitlementSource.Trial, ProductAccessStatus.Trial)]
    [InlineData(EntitlementSource.BetaInvite, ProductAccessStatus.BetaAccess)]
    [InlineData(EntitlementSource.AdminGrant, ProductAccessStatus.Gifted)]
    public void A_usable_grant_names_the_relationship(
        EntitlementSource source,
        ProductAccessStatus expected)
    {
        var decision = Evaluate(Product(), grant: new GrantFacts(source, IsUsable: true));

        Assert.Equal(expected, decision.Status);
    }

    [Fact]
    public void Access_through_membership_alone_reads_as_active()
    {
        Assert.Equal(ProductAccessStatus.Active, Evaluate(Product()).Status);
    }

    [Fact]
    public void Something_that_ran_out_reads_as_expired_not_locked()
    {
        // Expired is fixed by renewing; Locked is fixed by obtaining it. Collapsing them would
        // tell a lapsed member the wrong thing to do.
        var decision = Evaluate(
            Product(), allowed: false, reason: AccessDenialReason.MembershipInvalid);

        Assert.Equal(ProductAccessStatus.Expired, decision.Status);
    }

    [Fact]
    public void Something_never_held_and_not_for_sale_reads_as_locked()
    {
        var decision = Evaluate(
            Product(), allowed: false, reason: AccessDenialReason.NoEntitlement);

        Assert.Equal(ProductAccessStatus.Locked, decision.Status);
    }

    [Fact]
    public void Something_never_held_but_purchasable_reads_as_available_to_buy()
    {
        var decision = Evaluate(
            Product(capabilities: ProductCapability.Launchable | ProductCapability.Purchasable),
            allowed: false,
            reason: AccessDenialReason.NoEntitlement);

        Assert.Equal(ProductAccessStatus.AvailableToBuy, decision.Status);
    }

    [Fact]
    public void Coming_soon_outranks_every_other_status()
    {
        var decision = Evaluate(Product(release: ProductReleaseStatus.ComingSoon));

        Assert.Equal(ProductAccessStatus.ComingSoon, decision.Status);
        Assert.Equal(ProductPrimaryAction.ComingSoon, decision.PrimaryAction);
    }

    // ------------------------------------------------------------- purchase gating ----

    [Fact]
    public void Buying_is_impossible_while_the_purchase_feature_is_off()
    {
        // The flag defaults off, so this is the shipped behaviour until it is reviewed.
        var decision = Evaluate(
            Product(capabilities: ProductCapability.Purchasable),
            allowed: false,
            reason: AccessDenialReason.NoEntitlement,
            features: Features(purchases: false));

        Assert.False(decision.CanPurchase);
        Assert.NotEqual(ProductPrimaryAction.Buy, decision.PrimaryAction);
    }

    [Fact]
    public void Buying_becomes_possible_once_the_feature_is_on()
    {
        var decision = Evaluate(
            Product(capabilities: ProductCapability.Purchasable),
            allowed: false,
            reason: AccessDenialReason.NoEntitlement,
            features: Features(purchases: true));

        Assert.True(decision.CanPurchase);
        Assert.Equal(ProductPrimaryAction.Buy, decision.PrimaryAction);
    }

    [Fact]
    public void A_member_who_already_has_access_is_not_offered_it_for_sale()
    {
        var decision = Evaluate(
            Product(capabilities: ProductCapability.Launchable | ProductCapability.Purchasable),
            features: Features(purchases: true));

        Assert.False(decision.CanPurchase);
        Assert.Equal(ProductPrimaryAction.Open, decision.PrimaryAction);
    }

    [Fact]
    public void An_expired_renewable_product_leads_with_renew()
    {
        var decision = Evaluate(
            Product(capabilities: ProductCapability.Launchable | ProductCapability.Renewable),
            allowed: false,
            reason: AccessDenialReason.MembershipInvalid,
            grant: new GrantFacts(EntitlementSource.AdminGrant, IsUsable: false),
            features: Features(purchases: true));

        Assert.Equal(ProductPrimaryAction.Renew, decision.PrimaryAction);
    }

    [Fact]
    public void A_pre_release_product_cannot_be_bought_even_when_purchases_are_on()
    {
        // Selling something still in beta commits to supporting it at that quality.
        var decision = Evaluate(
            Product(capabilities: ProductCapability.Purchasable, release: ProductReleaseStatus.Beta),
            allowed: false,
            reason: AccessDenialReason.NoEntitlement,
            features: Features(purchases: true));

        Assert.False(decision.CanPurchase);
    }

    // ------------------------------------------------------------------ the one button ----

    [Fact]
    public void A_manageable_service_beats_a_download()
    {
        // Where the member's own state lives is more useful than a client installer.
        var decision = Evaluate(Product(
            capabilities: ProductCapability.HasConfigurations | ProductCapability.Downloadable,
            type: ProductType.SubscriptionService));

        Assert.Equal(ProductPrimaryAction.Manage, decision.PrimaryAction);
    }

    [Fact]
    public void A_visible_product_with_nothing_to_do_still_offers_its_details()
    {
        // Better than a dead card: the page explains what it is and how to obtain it.
        var decision = Evaluate(
            Product(capabilities: ProductCapability.HasDocumentation),
            allowed: false,
            reason: AccessDenialReason.NoEntitlement);

        Assert.True(decision.CanView);
        Assert.Equal(ProductPrimaryAction.ViewDetails, decision.PrimaryAction);
    }

    [Fact]
    public void A_locked_beta_product_offers_to_join_it()
    {
        var decision = Evaluate(
            Product(capabilities: ProductCapability.BetaAccess, release: ProductReleaseStatus.Beta),
            allowed: false,
            reason: AccessDenialReason.NoEntitlement);

        Assert.Equal(ProductPrimaryAction.JoinBeta, decision.PrimaryAction);
    }

    [Fact]
    public void A_visible_but_unusable_product_is_reported_as_locked_rather_than_usable()
    {
        var decision = Evaluate(
            Product(), allowed: false, reason: AccessDenialReason.MembershipInvalid);

        Assert.True(decision.IsVisibleButLocked);
        Assert.False(decision.IsUsable);
    }

    [Fact]
    public void Every_visible_decision_carries_an_action()
    {
        // A card with no button is a dead end. Whatever the combination, something is offered.
        var combinations =
            from release in new[]
            {
                ProductReleaseStatus.Beta, ProductReleaseStatus.Stable,
                ProductReleaseStatus.Deprecated, ProductReleaseStatus.ComingSoon,
            }
            from capability in new[]
            {
                ProductCapability.Launchable, ProductCapability.Downloadable,
                ProductCapability.HasConfigurations, ProductCapability.HasDocumentation,
                ProductCapability.Purchasable,
            }
            from allowed in new[] { true, false }
            select (release, capability, allowed);

        foreach (var (release, capability, allowed) in combinations)
        {
            var decision = Evaluate(
                Product(capabilities: capability, release: release),
                allowed,
                AccessDenialReason.NoEntitlement,
                features: Features(purchases: true));

            if (!decision.CanView)
            {
                continue;
            }

            Assert.True(
                decision.PrimaryAction != ProductPrimaryAction.None,
                $"{release}/{capability}/allowed={allowed} produced a visible card with no action.");
        }
    }
}
