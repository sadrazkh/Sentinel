using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Access;
using Sentinel.Application.Features;
using Sentinel.Application.Memberships;
using Sentinel.Application.Products;
using Sentinel.Domain.Entitlements;
using Sentinel.Domain.Products;

namespace Sentinel.Infrastructure.Products;

/// <summary>
/// Loads what the library needs and hands it to the rules. Two queries per page — products and
/// this member's grants — matched in memory, never a grant lookup per card.
/// </summary>
public sealed class ProductLibraryService : IProductLibraryService
{
    private readonly ISentinelDbContext _db;
    private readonly IMembershipStatusResolver _membershipResolver;
    private readonly IFeatureGate _features;
    private readonly TimeProvider _timeProvider;

    public ProductLibraryService(
        ISentinelDbContext db,
        IMembershipStatusResolver membershipResolver,
        IFeatureGate features,
        TimeProvider timeProvider)
    {
        _db = db;
        _membershipResolver = membershipResolver;
        _features = features;
        _timeProvider = timeProvider;
    }

    public async Task<ProductLibraryView> GetLibraryAsync(
        Guid userId,
        ProductLibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var now = _timeProvider.GetUtcNow();
        var flags = _features.Current;

        var subject = await LoadSubjectAsync(userId, cancellationToken);
        if (subject is null)
        {
            return new ProductLibraryView(MembershipSnapshot.None, [], [], query);
        }

        var membership = _membershipResolver.Resolve(subject.Membership, now);

        // Draft and Archived are filtered in SQL rather than after loading: they are internal
        // states, and a member has no business receiving the rows at all.
        var rows = await _db.Products
            .AsNoTracking()
            .Where(p => p.IsEnabled
                        && p.ReleaseStatus != ProductReleaseStatus.Draft
                        && p.ReleaseStatus != ProductReleaseStatus.Archived)
            .OrderByDescending(p => p.IsFeatured)
            .ThenBy(p => p.DisplayOrder)
            .ThenBy(p => p.NameEn)
            .Select(p => new ProductRow(
                new ProductFacts(
                    p.Id, p.Key, p.Type, p.Capabilities, p.ReleaseStatus, p.IsEnabled,
                    p.RequiresExplicitEntitlement, p.LaunchUrl != null, p.MinimumTier),
                p.NameFa, p.NameEn, p.SummaryFa, p.SummaryEn, p.IconPath, p.CurrentVersion,
                p.IsFeatured, p.DisplayOrder,
                p.Category == null ? null : p.Category.Key,
                p.Category == null ? null : p.Category.NameFa,
                p.Category == null ? null : p.Category.NameEn,
                p.Category == null || p.Category.IsVisible,
                p.Category == null ? int.MaxValue : p.Category.DisplayOrder,
                p.Category == null ? null : p.Category.IconName))
            .ToListAsync(cancellationToken);

        var grants = await LoadGrantsAsync(userId, cancellationToken);

        var cards = new List<ProductCard>(rows.Count);

        foreach (var row in rows)
        {
            grants.TryGetValue(row.Facts.Id, out var grant);

            var underlying = AccessRuleEvaluator.Evaluate(new AccessContext(
                subject.Account,
                membership,
                ToApplicationFacts(row.Facts),
                grant?.Facts,
                now));

            var access = ProductAccessRules.Evaluate(
                row.Facts, underlying, ToGrantFacts(grant, underlying), flags);

            if (!access.CanView)
            {
                continue;
            }

            cards.Add(BuildCard(row, access, grant, membership));
        }

        // Scope is applied before the counts so a category chip promises exactly what selecting
        // it would show. Counting across the whole catalogue would offer "Tools (2)" on a page
        // that, once clicked, has nothing in it.
        var inScope = ApplyScope(cards, query.Scope);

        // The card carries the category's name but not its display order or glyph, so the
        // filter list is built from the rows the cards came from.
        var rowsById = rows.ToDictionary(row => row.Facts.Id, row => row);

        var categories = inScope
            .Select(card => rowsById[card.Id])
            .Where(row => row.CategoryKey is not null && row.CategoryIsVisible)
            .GroupBy(row => (row.CategoryKey!, row.CategoryNameFa!, row.CategoryNameEn!,
                row.CategoryIconName, row.CategoryOrder))
            .OrderBy(group => group.Key.CategoryOrder)
            .ThenBy(group => group.Key.Item3, StringComparer.Ordinal)
            .Select(group => new ProductCategoryFilter(
                group.Key.Item1, group.Key.Item2, group.Key.Item3,
                group.Key.CategoryIconName, group.Count()))
            .ToList();

        var filtered = ApplyFilters(inScope, query);

        return new ProductLibraryView(membership, filtered, categories, query);
    }

    public async Task<ProductDetail?> GetDetailAsync(
        Guid userId,
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var normalizedKey = key.Trim().ToLowerInvariant();

        var row = await _db.Products
            .AsNoTracking()
            .Where(p => p.Key == normalizedKey)
            .Select(p => new DetailRow(
                new ProductRow(
                    new ProductFacts(
                        p.Id, p.Key, p.Type, p.Capabilities, p.ReleaseStatus, p.IsEnabled,
                        p.RequiresExplicitEntitlement, p.LaunchUrl != null, p.MinimumTier),
                    p.NameFa, p.NameEn, p.SummaryFa, p.SummaryEn, p.IconPath, p.CurrentVersion,
                    p.IsFeatured, p.DisplayOrder,
                    p.Category == null ? null : p.Category.Key,
                    p.Category == null ? null : p.Category.NameFa,
                    p.Category == null ? null : p.Category.NameEn,
                    p.Category == null || p.Category.IsVisible,
                    p.Category == null ? int.MaxValue : p.Category.DisplayOrder,
                    p.Category == null ? null : p.Category.IconName),
                p.DescriptionFa,
                p.DescriptionEn,
                p.CoverPath))
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var subject = await LoadSubjectAsync(userId, cancellationToken);
        if (subject is null)
        {
            return null;
        }

        var membership = _membershipResolver.Resolve(subject.Membership, now);
        var grant = await LoadGrantAsync(userId, row.Product.Facts.Id, cancellationToken);

        var underlying = AccessRuleEvaluator.Evaluate(new AccessContext(
            subject.Account,
            membership,
            ToApplicationFacts(row.Product.Facts),
            grant?.Facts,
            now));

        var access = ProductAccessRules.Evaluate(
            row.Product.Facts, underlying, ToGrantFacts(grant, underlying), _features.Current);

        // Invisible reads as absent. A 404 for "hidden" and a 200 for "locked" would let the
        // details URL enumerate what is being worked on.
        if (!access.CanView)
        {
            return null;
        }

        return new ProductDetail(
            BuildCard(row.Product, access, grant, membership),
            row.DescriptionFa,
            row.DescriptionEn,
            row.CoverPath,
            row.Product.Facts.Capabilities,
            row.Product.Facts.RequiresExplicitEntitlement,
            grant is null
                ? null
                : new ProductGrantSummary(
                    grant.Source,
                    grant.Facts.StartsAt,
                    grant.Facts.ExpiresAt,
                    IsGrantUsable(grant.Facts, now)));
    }

    // ------------------------------------------------------------------------ helpers ----

    /// <summary>
    /// "Mine" is what the member has a relationship with: usable now, or held and let lapse.
    /// Something merely on offer is not in their library.
    /// </summary>
    private static List<ProductCard> ApplyScope(
        List<ProductCard> cards,
        ProductLibraryScope scope) =>
        scope == ProductLibraryScope.Mine
            ? cards
                .Where(card => card.Access.IsUsable
                               || card.Access.Status == ProductAccessStatus.Expired)
                .ToList()
            : cards;

    private static IReadOnlyList<ProductCard> ApplyFilters(
        List<ProductCard> cards,
        ProductLibraryQuery query)
    {
        IEnumerable<ProductCard> result = cards;

        if (!string.IsNullOrWhiteSpace(query.CategoryKey))
        {
            result = result.Where(card =>
                string.Equals(card.CategoryKey, query.CategoryKey, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Matched in memory over an already-authorised set. The page is small by
            // construction — a member sees tens of products, not thousands — and doing it here
            // keeps the search term out of SQL entirely.
            var term = query.Search.Trim();

            result = result.Where(card =>
                Contains(card.NameFa, term)
                || Contains(card.NameEn, term)
                || Contains(card.SummaryFa, term)
                || Contains(card.SummaryEn, term)
                || Contains(card.Key, term));
        }

        return result.ToList();
    }

    private static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static ProductCard BuildCard(
        ProductRow row,
        ProductAccessDecision access,
        GrantRow? grant,
        MembershipSnapshot membership)
    {
        // What the member should watch: their grant's expiry when one carries their access,
        // otherwise the membership's. Whichever runs out first is the honest answer.
        var accessEndsAt = access.IsUsable
            ? Earliest(grant?.Facts.ExpiresAt, membership.AccessEndsAt)
            : null;

        return new ProductCard(
            row.Facts.Id,
            row.Facts.Key,
            row.Facts.Type,
            row.Facts.ReleaseStatus,
            row.NameFa,
            row.NameEn,
            row.SummaryFa,
            row.SummaryEn,
            row.IconPath,
            row.CurrentVersion,
            row.IsFeatured,
            row.DisplayOrder,
            row.Facts.MinimumTier,
            row.CategoryIsVisible ? row.CategoryKey : null,
            row.CategoryIsVisible ? row.CategoryNameFa : null,
            row.CategoryIsVisible ? row.CategoryNameEn : null,
            accessEndsAt,
            access);
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? left, DateTimeOffset? right) =>
        (left, right) switch
        {
            (null, null) => null,
            (null, { } r) => r,
            ({ } l, null) => l,
            ({ } l, { } r) => l < r ? l : r,
        };

    private static ApplicationFacts ToApplicationFacts(ProductFacts facts) => new(
        facts.Id, facts.Key, facts.IsEnabled, facts.ReleaseStatus,
        facts.RequiresExplicitEntitlement, facts.MinimumTier);

    /// <summary>
    /// A grant is "usable" for labelling purposes only when it is what actually carries the
    /// access. Reading it from the underlying decision rather than re-deriving it keeps the
    /// label and the security answer from disagreeing.
    /// </summary>
    private static GrantFacts? ToGrantFacts(GrantRow? grant, AccessDecision underlying) =>
        grant is null ? null : new GrantFacts(grant.Source, underlying.IsAllowed);

    private static bool IsGrantUsable(EntitlementFacts facts, DateTimeOffset now) =>
        facts.RevokedAt is null
        && facts.IsEnabled
        && now >= facts.StartsAt
        && (facts.ExpiresAt is not { } expiresAt || now <= expiresAt);

    private Task<SubjectFacts?> LoadSubjectAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new SubjectFacts(
                new AccountFacts(u.Status, u.SuspendedUntil),
                u.Membership == null
                    ? null
                    : new MembershipFacts(
                        u.Membership.Tier,
                        u.Membership.AdminState,
                        u.Membership.StartsAt,
                        u.Membership.EndsAt,
                        u.Membership.GracePeriodDaysOverride)))
            .FirstOrDefaultAsync(cancellationToken)!;

    private async Task<Dictionary<Guid, GrantRow>> LoadGrantsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var grants = await _db.ProductEntitlements
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .Select(e => new GrantRow(
                e.ProductId,
                e.Source,
                new EntitlementFacts(e.IsEnabled, e.StartsAt, e.ExpiresAt, e.RevokedAt)))
            .ToListAsync(cancellationToken);

        return grants.ToDictionary(g => g.ProductId);
    }

    private Task<GrantRow?> LoadGrantAsync(
        Guid userId,
        Guid productId,
        CancellationToken cancellationToken) =>
        _db.ProductEntitlements
            .AsNoTracking()
            .Where(e => e.UserId == userId && e.ProductId == productId)
            .Select(e => new GrantRow(
                e.ProductId,
                e.Source,
                new EntitlementFacts(e.IsEnabled, e.StartsAt, e.ExpiresAt, e.RevokedAt)))
            .FirstOrDefaultAsync(cancellationToken)!;

    private sealed record SubjectFacts(AccountFacts Account, MembershipFacts? Membership);

    private sealed record GrantRow(Guid ProductId, EntitlementSource Source, EntitlementFacts Facts);

    private sealed record ProductRow(
        ProductFacts Facts,
        string NameFa,
        string NameEn,
        string? SummaryFa,
        string? SummaryEn,
        string? IconPath,
        string? CurrentVersion,
        bool IsFeatured,
        int DisplayOrder,
        string? CategoryKey,
        string? CategoryNameFa,
        string? CategoryNameEn,
        bool CategoryIsVisible,
        int CategoryOrder,
        string? CategoryIconName);

    private sealed record DetailRow(
        ProductRow Product,
        string? DescriptionFa,
        string? DescriptionEn,
        string? CoverPath);
}
