using Sentinel.Application.Access;
using Sentinel.Application.Memberships;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Products;

namespace Sentinel.Application.Products;

/// <summary>
/// One product as one member sees it.
/// <para>
/// Carries no launch URL, no download URL and no configuration. Everything actionable goes
/// through an endpoint that re-checks access, so what reaches the browser is a label and a
/// link to the portal — never the destination itself.
/// </para>
/// </summary>
public sealed record ProductCard(
    Guid Id,
    string Key,
    ProductType Type,
    ProductReleaseStatus ReleaseStatus,
    string NameFa,
    string NameEn,
    string? SummaryFa,
    string? SummaryEn,
    string? IconPath,
    string? CurrentVersion,
    bool IsFeatured,
    int DisplayOrder,
    MembershipTier? MinimumTier,
    string? CategoryKey,
    string? CategoryNameFa,
    string? CategoryNameEn,
    /// <summary>When this member's access lapses, from whichever grant or membership carries it.</summary>
    DateTimeOffset? AccessEndsAt,
    ProductAccessDecision Access);

/// <summary>A category with the number of products this member can actually see in it.</summary>
public sealed record ProductCategoryFilter(
    string Key,
    string NameFa,
    string NameEn,
    string? IconName,
    int Count);

/// <summary>What the library was asked to show. Free text is matched server-side, never in SQL by concatenation.</summary>
public sealed record ProductLibraryQuery(
    string? CategoryKey = null,
    string? Search = null,
    ProductLibraryScope Scope = ProductLibraryScope.Discover);

public enum ProductLibraryScope
{
    /// <summary>Everything visible to this member, held or not.</summary>
    Discover = 0,

    /// <summary>Only what the member can use right now, plus what they held and let lapse.</summary>
    Mine = 1,
}

/// <summary>The library page: the member's membership, the visible products, and the category filters.</summary>
public sealed record ProductLibraryView(
    MembershipSnapshot Membership,
    IReadOnlyList<ProductCard> Products,
    IReadOnlyList<ProductCategoryFilter> Categories,
    ProductLibraryQuery Query)
{
    public int UsableCount => Products.Count(p => p.Access.IsUsable);

    /// <summary>
    /// Locked excludes "coming soon": nothing the member does would unlock something that has
    /// not shipped, so counting it as locked would overstate what they are missing out on.
    /// </summary>
    public int LockedCount => Products.Count(p =>
        p.Access.IsVisibleButLocked && p.Access.Status != ProductAccessStatus.ComingSoon);

    public int ComingSoonCount =>
        Products.Count(p => p.Access.Status == ProductAccessStatus.ComingSoon);
}

/// <summary>
/// The details page. Returned only when the member may view the product at all — an invisible
/// product yields <c>null</c>, so a details URL cannot be used to probe what exists.
/// </summary>
public sealed record ProductDetail(
    ProductCard Card,
    string? DescriptionFa,
    string? DescriptionEn,
    string? CoverPath,
    ProductCapability Capabilities,
    bool RequiresExplicitEntitlement,
    /// <summary>Present only when this member holds a grant — the source and dates behind their access.</summary>
    ProductGrantSummary? Grant);

/// <summary>The member's own grant, shown so they can see where their access came from and when it ends.</summary>
public sealed record ProductGrantSummary(
    Domain.Entitlements.EntitlementSource Source,
    DateTimeOffset StartsAt,
    DateTimeOffset? ExpiresAt,
    bool IsUsable);
