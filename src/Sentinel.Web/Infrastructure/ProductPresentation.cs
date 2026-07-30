using Sentinel.Application.Access;
using Sentinel.Application.Products;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Products;

namespace Sentinel.Web.Infrastructure;

/// <summary>
/// The button a card leads with: its label, where it goes, and how it is styled.
/// <para>
/// Always has a destination. A card with a button that does nothing is worse than a card with
/// no button, so an action whose real screen is not built yet resolves to the details page
/// rather than rendering as disabled.
/// </para>
/// </summary>
public sealed record ProductAction(string LabelKey, string Url, string CssClass);

/// <summary>
/// Maps the product enums onto localisation keys, badge styles and destinations.
/// <para>
/// One file rather than branches spread across templates: a new release status or access status
/// then fails to compile here instead of quietly rendering as a blank badge.
/// </para>
/// </summary>
public static class ProductPresentation
{
    public static string StatusKey(ProductAccessStatus status) => $"productStatus.{Lower(status)}";

    public static string ReleaseKey(ProductReleaseStatus status) => $"releaseStatus.{Lower(status)}";

    public static string TypeKey(ProductType type) => $"productType.{Lower(type)}";

    public static string SourceKey(EntitlementSource source) => $"entitlementSource.{Lower(source)}";

    public static string PlatformKey(DownloadPlatform platform) => $"platform.{Lower(platform)}";

    public static string SectionKindKey(ProductSectionKind kind) => $"sectionKind.{Lower(kind)}";

    public static string VisibilityKey(ContentVisibility visibility) => $"visibility.{Lower(visibility)}";

    /// <summary>
    /// The host part of a stored URL, for a list row. A full URL makes the row unreadable, and
    /// the operator sees the whole value on the edit page anyway.
    /// </summary>
    public static string HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "—";

    public static string StatusBadgeClass(ProductAccessStatus status) => status switch
    {
        ProductAccessStatus.Active => "badge--success",
        ProductAccessStatus.Owned => "badge--success",
        ProductAccessStatus.Gifted => "badge--success",
        ProductAccessStatus.Trial => "badge--info",
        ProductAccessStatus.BetaAccess => "badge--info",
        ProductAccessStatus.ComingSoon => "badge--info",
        ProductAccessStatus.Expired => "badge--danger",
        ProductAccessStatus.AvailableToBuy => "badge--warning",
        _ => "badge--neutral",
    };

    /// <summary>
    /// Turns the decided action into something the page can render.
    /// <para>
    /// Actions whose destination does not exist yet fall back to the details page rather than
    /// rendering a button that goes nowhere. Downloads arrive with the content phase and
    /// purchase with the wallet phase; until then a card that would offer them says
    /// "details" and means it.
    /// </para>
    /// </summary>
    public static ProductAction Describe(
        ProductPrimaryAction action,
        string productKey,
        Func<string, string> launchUrlFor,
        Func<string, string> detailsUrlFor)
    {
        var details = detailsUrlFor(productKey);

        return action switch
        {
            ProductPrimaryAction.Open =>
                new ProductAction("product.action.open", launchUrlFor(productKey), "btn btn--primary btn--sm"),

            // Everything else leads to the details page. "Coming soon" is already said by the
            // badge, so repeating it on a dead button would be noise where a live link to what
            // the product will be is useful. Manage, Download, Buy and Renew arrive with their
            // own phases; until then nothing promises a screen that does not exist.
            _ => new ProductAction("product.action.details", details, "btn btn--secondary btn--sm"),
        };
    }

    /// <summary>The lock message for a card the member can see but not use.</summary>
    public static string? LockReasonKey(ProductCard card) =>
        card.Access.IsVisibleButLocked && card.Access.Status != ProductAccessStatus.ComingSoon
            ? AccessPresentation.DenialReasonKey(card.Access.DenialReason)
            : null;

    /// <summary>
    /// Matches the key convention the admin views already use for release statuses, so there is
    /// one spelling of <c>releaseStatus.comingsoon</c> rather than two that differ by a capital.
    /// </summary>
    private static string Lower<TEnum>(TEnum value) where TEnum : struct, Enum =>
        value.ToString()!.ToLowerInvariant();
}
