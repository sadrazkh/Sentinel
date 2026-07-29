using Sentinel.Application.Access;
using Sentinel.Domain.Products;
using Sentinel.Domain.Memberships;

namespace Sentinel.Web.Infrastructure;

/// <summary>
/// Maps decision and status enums to the localisation keys and badge styles the views use.
/// <para>
/// Kept out of the views so that a new enum member is a compile-time concern in one file
/// rather than a silently missing branch in several <c>.cshtml</c> templates.
/// </para>
/// </summary>
public static class AccessPresentation
{
    public static string DenialReasonKey(AccessDenialReason reason) => reason switch
    {
        AccessDenialReason.AccountDisabled => "denial.accountDisabled",
        AccessDenialReason.AccountSuspended => "denial.accountSuspended",
        AccessDenialReason.ApplicationDisabled => "denial.applicationDisabled",
        AccessDenialReason.ApplicationNotPublished => "denial.applicationNotPublished",
        AccessDenialReason.ApplicationComingSoon => "denial.applicationComingSoon",
        AccessDenialReason.ApplicationRetired => "denial.applicationRetired",
        AccessDenialReason.MembershipInvalid => "denial.membershipInvalid",
        AccessDenialReason.TierTooLow => "denial.tierTooLow",
        AccessDenialReason.NoEntitlement => "denial.noEntitlement",
        AccessDenialReason.EntitlementDisabled => "denial.entitlementDisabled",
        AccessDenialReason.EntitlementRevoked => "denial.entitlementRevoked",
        AccessDenialReason.EntitlementNotStarted => "denial.entitlementNotStarted",
        AccessDenialReason.EntitlementExpired => "denial.entitlementExpired",
        _ => "denial.generic",
    };

    public static string MembershipStatusKey(MembershipStatus status) => status switch
    {
        MembershipStatus.None => "membershipStatus.none",
        MembershipStatus.Pending => "membershipStatus.pending",
        MembershipStatus.Active => "membershipStatus.active",
        MembershipStatus.GracePeriod => "membershipStatus.gracePeriod",
        MembershipStatus.Expired => "membershipStatus.expired",
        MembershipStatus.Suspended => "membershipStatus.suspended",
        MembershipStatus.Cancelled => "membershipStatus.cancelled",
        _ => "membershipStatus.none",
    };

    public static string MembershipBadgeClass(MembershipStatus status) => status switch
    {
        MembershipStatus.Active => "badge--success",
        MembershipStatus.GracePeriod => "badge--warning",
        MembershipStatus.Pending => "badge--info",
        MembershipStatus.Expired => "badge--danger",
        MembershipStatus.Suspended => "badge--danger",
        MembershipStatus.Cancelled => "badge--neutral",
        _ => "badge--neutral",
    };

    public static string TierKey(MembershipTier tier) => $"tier.{tier}";

    /// <summary>The single most useful badge for a card, chosen in priority order.</summary>
    public static (string CssClass, string LabelKey) CardStatusBadge(ApplicationCard card)
    {
        if (card.ReleaseStatus == ProductReleaseStatus.ComingSoon)
        {
            return ("badge--info", "appBadge.comingSoon");
        }

        if (card.ReleaseStatus == ProductReleaseStatus.Deprecated)
        {
            return ("badge--neutral", "appBadge.retired");
        }

        if (!card.CanLaunch)
        {
            // Expiry is worth calling out by name; every other lock reads as a generic lock.
            var isExpiry = card.Decision.Reason is
                AccessDenialReason.MembershipInvalid or AccessDenialReason.EntitlementExpired;

            return isExpiry
                ? ("badge--danger", "appBadge.expired")
                : ("badge--neutral", "appBadge.locked");
        }

        return ("badge--success", "appBadge.active");
    }
}
