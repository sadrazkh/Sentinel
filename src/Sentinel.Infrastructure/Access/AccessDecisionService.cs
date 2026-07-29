using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Access;
using Sentinel.Application.Memberships;
using Sentinel.Domain.Catalog;

namespace Sentinel.Infrastructure.Access;

/// <summary>
/// Loads the inputs an access decision needs and hands them to
/// <see cref="AccessRuleEvaluator"/>. It contains no rules of its own — that separation is
/// what keeps the rules exhaustively unit-testable and stops a second, subtly different copy
/// of them appearing here.
/// </summary>
public sealed class AccessDecisionService : IAccessDecisionService
{
    private readonly ISentinelDbContext _db;
    private readonly IMembershipStatusResolver _membershipResolver;
    private readonly TimeProvider _timeProvider;

    public AccessDecisionService(
        ISentinelDbContext db,
        IMembershipStatusResolver membershipResolver,
        TimeProvider timeProvider)
    {
        _db = db;
        _membershipResolver = membershipResolver;
        _timeProvider = timeProvider;
    }

    public async Task<AccessDecision> EvaluateAsync(
        Guid userId,
        Guid applicationId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var subject = await LoadSubjectAsync(userId, cancellationToken);
        if (subject is null)
        {
            return AccessDecision.Denied(AccessDenialReason.AccountDisabled);
        }

        var application = await _db.PortalApplications
            .AsNoTracking()
            .Where(a => a.Id == applicationId)
            .Select(a => new ApplicationFacts(
                a.Id, a.Key, a.IsEnabled, a.PublishStatus, a.RequiresExplicitEntitlement, a.MinimumTier))
            .FirstOrDefaultAsync(cancellationToken);

        if (application is null)
        {
            return AccessDecision.Denied(AccessDenialReason.ApplicationNotPublished);
        }

        var entitlement = await LoadEntitlementAsync(userId, applicationId, cancellationToken);

        return AccessRuleEvaluator.Evaluate(new AccessContext(
            subject.Account,
            _membershipResolver.Resolve(subject.Membership, now),
            application,
            entitlement,
            now));
    }

    public async Task<PortalCatalog> GetCatalogAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var subject = await LoadSubjectAsync(userId, cancellationToken);
        if (subject is null)
        {
            return new PortalCatalog(MembershipSnapshot.None, []);
        }

        var membership = _membershipResolver.Resolve(subject.Membership, now);

        // Draft applications are filtered out in SQL: they are an internal state and a member
        // has no business knowing they exist.
        var applications = await _db.PortalApplications
            .AsNoTracking()
            .Where(a => a.PublishStatus != ApplicationPublishStatus.Draft)
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.NameEn)
            .Select(a => new CatalogRow(
                new ApplicationFacts(
                    a.Id, a.Key, a.IsEnabled, a.PublishStatus, a.RequiresExplicitEntitlement, a.MinimumTier),
                a.NameFa,
                a.NameEn,
                a.DescriptionFa,
                a.DescriptionEn,
                a.IconPath,
                a.IsBeta,
                a.DisplayOrder))
            .ToListAsync(cancellationToken);

        // All of this member's grants in one query, then matched in memory. Fetching a grant
        // per application would be the classic N+1.
        var entitlements = await _db.UserEntitlements
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .Select(e => new EntitlementRow(
                e.ApplicationId,
                new EntitlementFacts(e.IsEnabled, e.StartsAt, e.ExpiresAt, e.RevokedAt)))
            .ToListAsync(cancellationToken);

        var entitlementsByApplication = entitlements.ToDictionary(e => e.ApplicationId, e => e.Facts);

        var cards = applications
            .Select(row =>
            {
                entitlementsByApplication.TryGetValue(row.Application.Id, out var entitlement);

                var decision = AccessRuleEvaluator.Evaluate(new AccessContext(
                    subject.Account, membership, row.Application, entitlement, now));

                return new ApplicationCard(
                    row.Application.Id,
                    row.Application.Key,
                    row.NameFa,
                    row.NameEn,
                    row.DescriptionFa,
                    row.DescriptionEn,
                    row.IconPath,
                    row.IsBeta,
                    row.Application.PublishStatus,
                    row.DisplayOrder,
                    row.Application.MinimumTier,
                    decision);
            })
            // A disabled application is hidden entirely; a locked one stays visible so the
            // member can see what renewing would unlock.
            .Where(card => card.Decision.IsAllowed || card.Decision.IsVisibleButLocked)
            .ToList();

        return new PortalCatalog(membership, cards);
    }

    public async Task<LaunchResolution?> ResolveLaunchAsync(
        Guid userId,
        string applicationKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationKey))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var normalizedKey = applicationKey.Trim().ToLowerInvariant();

        var application = await _db.PortalApplications
            .AsNoTracking()
            .Where(a => a.Key == normalizedKey)
            .Select(a => new LaunchRow(
                new ApplicationFacts(
                    a.Id, a.Key, a.IsEnabled, a.PublishStatus, a.RequiresExplicitEntitlement, a.MinimumTier),
                a.NameFa,
                a.NameEn,
                a.LaunchUrl))
            .FirstOrDefaultAsync(cancellationToken);

        if (application is null)
        {
            return null;
        }

        var subject = await LoadSubjectAsync(userId, cancellationToken);
        if (subject is null)
        {
            return new LaunchResolution(
                application.Application.Id,
                application.Application.Key,
                application.NameEn,
                AccessDecision.Denied(AccessDenialReason.AccountDisabled),
                LaunchUrl: null);
        }

        var entitlement = await LoadEntitlementAsync(userId, application.Application.Id, cancellationToken);

        var decision = AccessRuleEvaluator.Evaluate(new AccessContext(
            subject.Account,
            _membershipResolver.Resolve(subject.Membership, now),
            application.Application,
            entitlement,
            now));

        return new LaunchResolution(
            application.Application.Id,
            application.Application.Key,
            application.NameEn,
            decision,
            // The destination is attached only on success, so a refused launch cannot be
            // turned into a way of reading the URL.
            decision.IsAllowed ? application.LaunchUrl : null);
    }

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

    private Task<EntitlementFacts?> LoadEntitlementAsync(
        Guid userId,
        Guid applicationId,
        CancellationToken cancellationToken) =>
        _db.UserEntitlements
            .AsNoTracking()
            .Where(e => e.UserId == userId && e.ApplicationId == applicationId)
            .Select(e => new EntitlementFacts(e.IsEnabled, e.StartsAt, e.ExpiresAt, e.RevokedAt))
            .FirstOrDefaultAsync(cancellationToken)!;

    private sealed record SubjectFacts(AccountFacts Account, MembershipFacts? Membership);

    private sealed record CatalogRow(
        ApplicationFacts Application,
        string NameFa,
        string NameEn,
        string? DescriptionFa,
        string? DescriptionEn,
        string? IconPath,
        bool IsBeta,
        int DisplayOrder);

    private sealed record LaunchRow(
        ApplicationFacts Application,
        string NameFa,
        string NameEn,
        string LaunchUrl);

    private sealed record EntitlementRow(Guid ApplicationId, EntitlementFacts Facts);
}
