using Sentinel.Application.Memberships;
using Sentinel.Application.Security;
using Sentinel.Domain.Products;
using Sentinel.Domain.Identity;

namespace Sentinel.Application.Access;

/// <summary>
/// Everything needed to decide one (user, application) question, gathered in one place so the
/// decision is a pure function of its inputs.
/// </summary>
public sealed record AccessContext(
    AccountFacts Account,
    MembershipSnapshot Membership,
    ApplicationFacts Application,
    EntitlementFacts? Entitlement,
    DateTimeOffset Now);

/// <summary>
/// The authorisation rules for launching an application, as a pure function.
/// <para>
/// Separated from <c>IAccessDecisionService</c> — which only loads the inputs — so the rules
/// can be tested exhaustively without a database, and so there is exactly one implementation
/// of "may this member open this application?" for both the catalogue listing and the launch
/// endpoint to call. A listing that computed availability differently from the endpoint would
/// eventually disagree with it, and the disagreement would be the security bug.
/// </para>
/// </summary>
public static class AccessRuleEvaluator
{
    public static AccessDecision Evaluate(AccessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 1. The account itself. Nothing below matters if the person may not be here at all.
        if (context.Account.Status == UserAccountStatus.Disabled)
        {
            return AccessDecision.Denied(AccessDenialReason.AccountDisabled);
        }

        if (context.Account.Status == UserAccountStatus.Suspended
            && !SuspensionHasLapsed(context.Account, context.Now))
        {
            return AccessDecision.Denied(AccessDenialReason.AccountSuspended);
        }

        // 2. The application. A disabled or unpublished application is closed to everyone,
        // including a member holding an explicit grant for it.
        if (!context.Application.IsEnabled)
        {
            return AccessDecision.Denied(AccessDenialReason.ApplicationDisabled);
        }

        var publishDenial = context.Application.ReleaseStatus switch
        {
            ProductReleaseStatus.Draft => AccessDenialReason.ApplicationNotPublished,
            ProductReleaseStatus.ComingSoon => AccessDenialReason.ApplicationComingSoon,
            ProductReleaseStatus.Deprecated => AccessDenialReason.ApplicationRetired,
            _ => AccessDenialReason.None,
        };

        if (publishDenial != AccessDenialReason.None)
        {
            return AccessDecision.Denied(publishDenial);
        }

        // 3. An explicit grant. A usable one is sufficient on its own — that is the point of
        // entitlements: they let an individual keep an application without a matching tier,
        // or without a live membership at all.
        var entitlement = EvaluateEntitlement(context.Entitlement, context.Now);

        if (entitlement == EntitlementState.Usable)
        {
            return AccessDecision.Allowed;
        }

        // 4. Applications marked as requiring an explicit grant stop here: membership alone
        // never unlocks them.
        if (context.Application.RequiresExplicitEntitlement)
        {
            return AccessDecision.Denied(DescribeEntitlementFailure(entitlement));
        }

        // 5. Otherwise the membership decides.
        if (!context.Membership.GrantsAccess)
        {
            return AccessDecision.Denied(AccessDenialReason.MembershipInvalid);
        }

        if (context.Application.MinimumTier is { } minimumTier
            && (context.Membership.Tier is not { } tier || tier < minimumTier))
        {
            return AccessDecision.Denied(AccessDenialReason.TierTooLow);
        }

        return AccessDecision.Allowed;
    }

    private enum EntitlementState
    {
        Missing,
        Usable,
        Disabled,
        Revoked,
        NotStarted,
        Expired,
    }

    private static EntitlementState EvaluateEntitlement(EntitlementFacts? facts, DateTimeOffset now)
    {
        if (facts is null)
        {
            return EntitlementState.Missing;
        }

        // Revocation is checked first: a revoked grant is revoked regardless of its dates.
        if (facts.RevokedAt is not null)
        {
            return EntitlementState.Revoked;
        }

        if (!facts.IsEnabled)
        {
            return EntitlementState.Disabled;
        }

        if (now < facts.StartsAt)
        {
            return EntitlementState.NotStarted;
        }

        if (facts.ExpiresAt is { } expiresAt && now > expiresAt)
        {
            return EntitlementState.Expired;
        }

        return EntitlementState.Usable;
    }

    private static AccessDenialReason DescribeEntitlementFailure(EntitlementState state) => state switch
    {
        EntitlementState.Disabled => AccessDenialReason.EntitlementDisabled,
        EntitlementState.Revoked => AccessDenialReason.EntitlementRevoked,
        EntitlementState.NotStarted => AccessDenialReason.EntitlementNotStarted,
        EntitlementState.Expired => AccessDenialReason.EntitlementExpired,
        _ => AccessDenialReason.NoEntitlement,
    };

    private static bool SuspensionHasLapsed(AccountFacts account, DateTimeOffset now) =>
        account.SuspendedUntil is { } until && until <= now;

    /// <summary>
    /// Convenience overload used where an <see cref="ApplicationUser"/> is already loaded, so
    /// that account-status handling stays consistent with <see cref="AccountSignInRules"/>.
    /// </summary>
    public static AccountFacts DescribeAccount(ApplicationUser user) =>
        new(user.Status, user.SuspendedUntil);
}
