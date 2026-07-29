using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Access;
using Sentinel.Application.Entitlements;
using Sentinel.Application.Memberships;

namespace Sentinel.Infrastructure.Entitlements;

public sealed class EntitlementAdminQuery : IEntitlementAdminQuery
{
    private readonly ISentinelDbContext _db;
    private readonly IMembershipStatusResolver _membershipResolver;
    private readonly TimeProvider _timeProvider;

    public EntitlementAdminQuery(
        ISentinelDbContext db,
        IMembershipStatusResolver membershipResolver,
        TimeProvider timeProvider)
    {
        _db = db;
        _membershipResolver = membershipResolver;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<UserApplicationGrantRow>> GetForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var subject = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                Account = new AccountFacts(u.Status, u.SuspendedUntil),
                Membership = u.Membership == null
                    ? null
                    : new MembershipFacts(
                        u.Membership.Tier,
                        u.Membership.AdminState,
                        u.Membership.StartsAt,
                        u.Membership.EndsAt,
                        u.Membership.GracePeriodDaysOverride),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (subject is null)
        {
            return [];
        }

        var membership = _membershipResolver.Resolve(subject.Membership, now);

        // Drafts are included here, unlike the member-facing catalogue: an operator needs to
        // be able to pre-grant access to something not yet published.
        var applications = await _db.Products
            .AsNoTracking()
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.NameEn)
            .Select(a => new
            {
                Facts = new ApplicationFacts(
                    a.Id, a.Key, a.IsEnabled, a.ReleaseStatus,
                    a.RequiresExplicitEntitlement, a.MinimumTier),
                a.NameFa,
                a.NameEn,
            })
            .ToListAsync(cancellationToken);

        // One query for every grant this user holds, then matched in memory — a lookup per
        // application would be the classic N+1.
        var grants = await _db.ProductEntitlements
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .Select(e => new
            {
                e.ProductId,
                e.IsEnabled,
                e.StartsAt,
                e.ExpiresAt,
                e.RevokedAt,
                e.Notes,
                e.ConcurrencyToken,
            })
            .ToListAsync(cancellationToken);

        var grantsByApplication = grants.ToDictionary(g => g.ProductId);

        return applications
            .Select(application =>
            {
                grantsByApplication.TryGetValue(application.Facts.Id, out var grant);

                var entitlement = grant is null
                    ? null
                    : new EntitlementFacts(grant.IsEnabled, grant.StartsAt, grant.ExpiresAt, grant.RevokedAt);

                // The same evaluator the portal uses, so this table shows what the member
                // would actually experience rather than a separate guess at it.
                var decision = AccessRuleEvaluator.Evaluate(new AccessContext(
                    subject.Account, membership, application.Facts, entitlement, now));

                return new UserApplicationGrantRow(
                    application.Facts.Id,
                    application.Facts.Key,
                    application.NameFa,
                    application.NameEn,
                    application.Facts.ReleaseStatus,
                    application.Facts.IsEnabled,
                    application.Facts.RequiresExplicitEntitlement,
                    grant is not null,
                    grant?.IsEnabled ?? false,
                    grant?.StartsAt,
                    grant?.ExpiresAt,
                    grant?.RevokedAt,
                    grant?.Notes,
                    grant?.ConcurrencyToken,
                    decision);
            })
            .ToList();
    }
}
