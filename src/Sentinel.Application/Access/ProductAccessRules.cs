using Sentinel.Application.Features;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Products;

namespace Sentinel.Application.Access;

/// <summary>The product-side inputs to a product access decision.</summary>
public sealed record ProductFacts(
    Guid Id,
    string Key,
    ProductType Type,
    ProductCapability Capabilities,
    ProductReleaseStatus ReleaseStatus,
    bool IsEnabled,
    bool RequiresExplicitEntitlement,
    bool HasLaunchUrl,
    Domain.Memberships.MembershipTier? MinimumTier);

/// <summary>How a member came to hold a grant, when they hold one.</summary>
public sealed record GrantFacts(EntitlementSource Source, bool IsUsable);

/// <summary>
/// Turns the underlying access decision into what the library shows and offers.
/// <para>
/// Layered on top of <see cref="AccessRuleEvaluator"/> rather than replacing it: that evaluator
/// answers the one security question — may this member use this product — and is what the
/// launch endpoint enforces. This adds the presentation on top: which status to label it, which
/// single button to lead with, what a member may do besides launch it.
/// </para>
/// <para>
/// Keeping the security answer underneath and unchanged means adding a new call-to-action can
/// never accidentally widen who gets in.
/// </para>
/// </summary>
public static class ProductAccessRules
{
    public static ProductAccessDecision Evaluate(
        ProductFacts product,
        AccessDecision underlying,
        GrantFacts? grant,
        FeatureFlags features)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(underlying);
        ArgumentNullException.ThrowIfNull(features);

        // Internal states never surface, whatever the member holds.
        if (!product.IsEnabled
            || product.ReleaseStatus is ProductReleaseStatus.Draft or ProductReleaseStatus.Archived)
        {
            return ProductAccessDecision.Hidden;
        }

        // With beta products switched off, anything pre-release is invisible unless the member
        // was specifically invited — an invitation already issued is not revoked by a flag.
        var isPreRelease = product.ReleaseStatus
            is ProductReleaseStatus.PrivatePreview or ProductReleaseStatus.Alpha
            or ProductReleaseStatus.Beta;

        var holdsGrant = grant is { IsUsable: true };

        if (isPreRelease && !features.BetaProductsEnabled && !holdsGrant)
        {
            return ProductAccessDecision.Hidden;
        }

        // Private preview and alpha are invitation-only: a membership does not open them.
        if (product.ReleaseStatus is ProductReleaseStatus.PrivatePreview or ProductReleaseStatus.Alpha
            && !holdsGrant)
        {
            return ProductAccessDecision.Hidden;
        }

        var capabilities = product.Capabilities;
        var allowed = underlying.IsAllowed;

        var canLaunch = allowed && capabilities.Has(ProductCapability.Launchable) && product.HasLaunchUrl;
        var canDownload = allowed && capabilities.Has(ProductCapability.Downloadable);
        var canManage = allowed && capabilities.Has(ProductCapability.HasConfigurations);
        var canViewPrivateDocs = allowed && capabilities.Has(ProductCapability.HasDocumentation);

        // Buying needs the capability, the feature, and for the member not to already hold it.
        var canPurchase = capabilities.Has(ProductCapability.Purchasable)
                          && features.PurchasesEnabled
                          && product.ReleaseStatus == ProductReleaseStatus.Stable
                          && !allowed;

        var canRenew = capabilities.Has(ProductCapability.Renewable)
                       && features.PurchasesEnabled
                       && grant is not null;

        var status = DescribeStatus(product, underlying, grant, allowed);

        return new ProductAccessDecision(
            status,
            ChooseAction(product, status, canLaunch, canDownload, canManage, canPurchase, canRenew),
            CanView: true,
            canLaunch,
            canDownload,
            canPurchase,
            canRenew,
            canViewPrivateDocs,
            canManage,
            underlying.Reason);
    }

    private static ProductAccessStatus DescribeStatus(
        ProductFacts product,
        AccessDecision underlying,
        GrantFacts? grant,
        bool allowed)
    {
        if (product.ReleaseStatus == ProductReleaseStatus.ComingSoon)
        {
            return ProductAccessStatus.ComingSoon;
        }

        if (allowed)
        {
            // A usable grant names the relationship; otherwise access came from membership.
            return grant switch
            {
                { IsUsable: true, Source: EntitlementSource.Purchase } => ProductAccessStatus.Owned,
                { IsUsable: true, Source: EntitlementSource.Trial } => ProductAccessStatus.Trial,
                { IsUsable: true, Source: EntitlementSource.BetaInvite } => ProductAccessStatus.BetaAccess,
                { IsUsable: true, Source: EntitlementSource.AdminGrant } => ProductAccessStatus.Gifted,
                _ => ProductAccessStatus.Active,
            };
        }

        // Something that ran out reads differently from something never held: one is fixed by
        // renewing, the other by obtaining it.
        var ranOut = underlying.Reason is AccessDenialReason.MembershipInvalid
            or AccessDenialReason.EntitlementExpired;

        if (ranOut)
        {
            return ProductAccessStatus.Expired;
        }

        return product.Capabilities.Has(ProductCapability.Purchasable)
            ? ProductAccessStatus.AvailableToBuy
            : ProductAccessStatus.Locked;
    }

    /// <summary>
    /// The one button a card leads with, in descending order of usefulness to the member.
    /// </summary>
    private static ProductPrimaryAction ChooseAction(
        ProductFacts product,
        ProductAccessStatus status,
        bool canLaunch,
        bool canDownload,
        bool canManage,
        bool canPurchase,
        bool canRenew)
    {
        if (status == ProductAccessStatus.ComingSoon)
        {
            return ProductPrimaryAction.ComingSoon;
        }

        if (canLaunch)
        {
            return ProductPrimaryAction.Open;
        }

        // A service the member manages beats a raw download: it is where their state lives.
        if (canManage)
        {
            return ProductPrimaryAction.Manage;
        }

        if (canDownload)
        {
            return ProductPrimaryAction.Download;
        }

        if (status == ProductAccessStatus.Expired && canRenew)
        {
            return ProductPrimaryAction.Renew;
        }

        if (canPurchase)
        {
            return ProductPrimaryAction.Buy;
        }

        if (product.Capabilities.Has(ProductCapability.BetaAccess)
            && status is ProductAccessStatus.Locked or ProductAccessStatus.AvailableToBuy)
        {
            return ProductPrimaryAction.JoinBeta;
        }

        // Nothing actionable, but the details page still explains what the product is and how
        // to obtain it — which is better than a dead card.
        return ProductPrimaryAction.ViewDetails;
    }
}
