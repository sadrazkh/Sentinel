using Sentinel.Application.Memberships;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Notifications;

namespace Sentinel.Application.Notifications;

/// <summary>
/// Why a warning is being raised. Distinct from the stage: the stage decides whether a member
/// has already been told, the reason decides what the message says.
/// </summary>
public enum ExpiryNoticeReason
{
    None = 0,

    MembershipRenewalDue = 1,
    MembershipGracePeriod = 2,
    MembershipExpired = 3,

    SubscriptionExpiringSoon = 10,
    SubscriptionQuotaLow = 11,
    SubscriptionExpired = 12,
    SubscriptionQuotaExhausted = 13,
}

/// <summary>
/// Decides what a member should be warned about, and how loudly.
/// <para>
/// Pure, and separate from the job that sends anything, because the hard part of a recurring
/// notifier is not the timer — it is not telling somebody the same thing every hour. That is
/// decided here, by comparing the stage a subject is in now against the stage it was in when it
/// was last mentioned.
/// </para>
/// </summary>
public static class ExpiryNoticeRules
{
    /// <summary>Percentage of quota used at which a subscription warning is raised.</summary>
    public const int DefaultQuotaWarningPercent = 85;

    public static ExpiryNoticeStage StageFor(ExpiryNoticeReason reason) => reason switch
    {
        ExpiryNoticeReason.None => ExpiryNoticeStage.None,

        ExpiryNoticeReason.MembershipRenewalDue => ExpiryNoticeStage.Warning,
        ExpiryNoticeReason.SubscriptionExpiringSoon => ExpiryNoticeStage.Warning,
        ExpiryNoticeReason.SubscriptionQuotaLow => ExpiryNoticeStage.Warning,

        // Still usable, but the paid window has already closed.
        ExpiryNoticeReason.MembershipGracePeriod => ExpiryNoticeStage.Critical,

        ExpiryNoticeReason.MembershipExpired => ExpiryNoticeStage.Ended,
        ExpiryNoticeReason.SubscriptionExpired => ExpiryNoticeStage.Ended,
        ExpiryNoticeReason.SubscriptionQuotaExhausted => ExpiryNoticeStage.Ended,

        _ => ExpiryNoticeStage.None,
    };

    /// <summary>
    /// Whether a subject in <paramref name="current"/> deserves a message given that
    /// <paramref name="alreadyTold"/> was the last stage it was mentioned at.
    /// <para>
    /// Only an advance produces a message. Moving backwards — which is what a renewal looks
    /// like — produces none, but the caller still records the lower stage, and that is what
    /// re-arms the warnings for the next cycle. Without it, a membership renewed once would
    /// never warn again.
    /// </para>
    /// </summary>
    public static bool ShouldNotify(ExpiryNoticeStage current, ExpiryNoticeStage alreadyTold) =>
        current > alreadyTold;

    public static ExpiryNoticeReason EvaluateMembership(MembershipSnapshot membership) =>
        membership.Status switch
        {
            MembershipStatus.Expired => ExpiryNoticeReason.MembershipExpired,
            MembershipStatus.GracePeriod => ExpiryNoticeReason.MembershipGracePeriod,

            // Only an active membership counts down; Pending, Suspended and Cancelled are
            // administrator decisions the member cannot act on by renewing.
            MembershipStatus.Active when membership.IsRenewalDueSoon =>
                ExpiryNoticeReason.MembershipRenewalDue,

            _ => ExpiryNoticeReason.None,
        };

    /// <summary>
    /// Most severe applicable reason for a subscription. A source can be both nearly out of
    /// data and nearly out of time; saying both would be two messages about one problem.
    /// </summary>
    public static ExpiryNoticeReason EvaluateSubscription(
        DateTimeOffset? expiresAt,
        long? totalBytes,
        long? usedBytes,
        DateTimeOffset now,
        int warningDays,
        int quotaWarningPercent = DefaultQuotaWarningPercent)
    {
        if (expiresAt is { } expires && expires <= now)
        {
            return ExpiryNoticeReason.SubscriptionExpired;
        }

        // A total of zero means unlimited on every panel that reports one.
        var hasQuota = totalBytes is > 0;

        if (hasQuota && usedBytes >= totalBytes)
        {
            return ExpiryNoticeReason.SubscriptionQuotaExhausted;
        }

        if (expiresAt is { } soon && soon <= now.AddDays(Math.Max(0, warningDays)))
        {
            return ExpiryNoticeReason.SubscriptionExpiringSoon;
        }

        if (hasQuota && usedBytes is { } used)
        {
            var percent = used * 100 / totalBytes!.Value;

            if (percent >= quotaWarningPercent)
            {
                return ExpiryNoticeReason.SubscriptionQuotaLow;
            }
        }

        return ExpiryNoticeReason.None;
    }

    /// <summary>Localisation key for the message title, resolved against the recipient's culture.</summary>
    public static string TitleKey(ExpiryNoticeReason reason) => $"notice.{Slug(reason)}.title";

    public static string BodyKey(ExpiryNoticeReason reason) => $"notice.{Slug(reason)}.body";

    public static string LinkPath(ExpiryNoticeReason reason) =>
        reason is ExpiryNoticeReason.SubscriptionExpiringSoon
            or ExpiryNoticeReason.SubscriptionExpired
            or ExpiryNoticeReason.SubscriptionQuotaLow
            or ExpiryNoticeReason.SubscriptionQuotaExhausted
            ? "/Configs"
            : "/Membership";

    public static NotificationKind KindFor(ExpiryNoticeReason reason) =>
        reason is ExpiryNoticeReason.SubscriptionExpiringSoon
            or ExpiryNoticeReason.SubscriptionExpired
            or ExpiryNoticeReason.SubscriptionQuotaLow
            or ExpiryNoticeReason.SubscriptionQuotaExhausted
            ? NotificationKind.Subscription
            : NotificationKind.Membership;

    private static string Slug(ExpiryNoticeReason reason) => reason switch
    {
        ExpiryNoticeReason.MembershipRenewalDue => "membershipRenewalDue",
        ExpiryNoticeReason.MembershipGracePeriod => "membershipGracePeriod",
        ExpiryNoticeReason.MembershipExpired => "membershipExpired",
        ExpiryNoticeReason.SubscriptionExpiringSoon => "subscriptionExpiringSoon",
        ExpiryNoticeReason.SubscriptionQuotaLow => "subscriptionQuotaLow",
        ExpiryNoticeReason.SubscriptionExpired => "subscriptionExpired",
        ExpiryNoticeReason.SubscriptionQuotaExhausted => "subscriptionQuotaExhausted",
        _ => "none",
    };
}
