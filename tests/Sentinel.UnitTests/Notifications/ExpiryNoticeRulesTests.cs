using Sentinel.Application.Memberships;
using Sentinel.Application.Notifications;
using Sentinel.Domain.Memberships;
using Sentinel.Domain.Notifications;

namespace Sentinel.UnitTests.Notifications;

/// <summary>
/// The rules behind the recurring notifier. The timer is trivial; not telling somebody the
/// same thing every hour is the part that has to be right.
/// </summary>
public sealed class ExpiryNoticeRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static MembershipSnapshot Membership(
        MembershipStatus status,
        bool renewalDueSoon = false,
        int? daysRemaining = null) =>
        new(status, MembershipTier.Pro, Now.AddDays(-30), Now.AddDays(10),
            Now.AddDays(10), daysRemaining, renewalDueSoon);

    // ------------------------------------------------------------------- deduplication ----

    [Theory]
    [InlineData(ExpiryNoticeStage.Warning, ExpiryNoticeStage.None, true)]
    [InlineData(ExpiryNoticeStage.Critical, ExpiryNoticeStage.Warning, true)]
    [InlineData(ExpiryNoticeStage.Ended, ExpiryNoticeStage.Critical, true)]
    [InlineData(ExpiryNoticeStage.Ended, ExpiryNoticeStage.None, true)]
    public void An_advance_produces_a_message(
        ExpiryNoticeStage current,
        ExpiryNoticeStage alreadyTold,
        bool expected)
    {
        Assert.Equal(expected, ExpiryNoticeRules.ShouldNotify(current, alreadyTold));
    }

    [Theory]
    [InlineData(ExpiryNoticeStage.Warning, ExpiryNoticeStage.Warning)]
    [InlineData(ExpiryNoticeStage.Ended, ExpiryNoticeStage.Ended)]
    [InlineData(ExpiryNoticeStage.None, ExpiryNoticeStage.None)]
    public void Staying_at_the_same_stage_says_nothing(
        ExpiryNoticeStage current,
        ExpiryNoticeStage alreadyTold)
    {
        // This is the whole point: an hourly sweep must not repeat itself.
        Assert.False(ExpiryNoticeRules.ShouldNotify(current, alreadyTold));
    }

    [Theory]
    [InlineData(ExpiryNoticeStage.None, ExpiryNoticeStage.Ended)]
    [InlineData(ExpiryNoticeStage.Warning, ExpiryNoticeStage.Critical)]
    public void Moving_backwards_says_nothing(
        ExpiryNoticeStage current,
        ExpiryNoticeStage alreadyTold)
    {
        // Which is what a renewal looks like. The caller still records the lower stage, and
        // that is what re-arms the warnings for the next cycle.
        Assert.False(ExpiryNoticeRules.ShouldNotify(current, alreadyTold));
    }

    [Fact]
    public void The_stage_ordering_is_the_contract()
    {
        // ShouldNotify is a comparison, so the numeric ordering is load-bearing. A new stage
        // must be inserted between existing values, never appended.
        Assert.True(ExpiryNoticeStage.None < ExpiryNoticeStage.Warning);
        Assert.True(ExpiryNoticeStage.Warning < ExpiryNoticeStage.Critical);
        Assert.True(ExpiryNoticeStage.Critical < ExpiryNoticeStage.Ended);
    }

    // ---------------------------------------------------------------------- memberships ----

    [Fact]
    public void An_approaching_membership_expiry_raises_a_warning()
    {
        var reason = ExpiryNoticeRules.EvaluateMembership(
            Membership(MembershipStatus.Active, renewalDueSoon: true, daysRemaining: 3));

        Assert.Equal(ExpiryNoticeReason.MembershipRenewalDue, reason);
        Assert.Equal(ExpiryNoticeStage.Warning, ExpiryNoticeRules.StageFor(reason));
    }

    [Fact]
    public void A_healthy_membership_raises_nothing()
    {
        var reason = ExpiryNoticeRules.EvaluateMembership(
            Membership(MembershipStatus.Active, renewalDueSoon: false, daysRemaining: 200));

        Assert.Equal(ExpiryNoticeReason.None, reason);
    }

    [Fact]
    public void A_grace_period_is_louder_than_a_countdown_but_not_the_end()
    {
        var reason = ExpiryNoticeRules.EvaluateMembership(Membership(MembershipStatus.GracePeriod));

        Assert.Equal(ExpiryNoticeReason.MembershipGracePeriod, reason);
        Assert.Equal(ExpiryNoticeStage.Critical, ExpiryNoticeRules.StageFor(reason));
    }

    [Fact]
    public void An_expired_membership_is_the_end_stage()
    {
        var reason = ExpiryNoticeRules.EvaluateMembership(Membership(MembershipStatus.Expired));

        Assert.Equal(ExpiryNoticeReason.MembershipExpired, reason);
        Assert.Equal(ExpiryNoticeStage.Ended, ExpiryNoticeRules.StageFor(reason));
    }

    [Theory]
    [InlineData(MembershipStatus.Pending)]
    [InlineData(MembershipStatus.Suspended)]
    [InlineData(MembershipStatus.Cancelled)]
    [InlineData(MembershipStatus.None)]
    public void A_state_the_member_cannot_fix_by_renewing_raises_nothing(MembershipStatus status)
    {
        // Suspended and cancelled are administrator decisions; a "renew soon" nudge would be
        // both wrong and confusing.
        Assert.Equal(ExpiryNoticeReason.None, ExpiryNoticeRules.EvaluateMembership(Membership(status)));
    }

    [Fact]
    public void A_membership_progresses_through_each_stage_exactly_once()
    {
        // The lifecycle a real membership walks: healthy, counting down, grace, gone.
        var told = ExpiryNoticeStage.None;

        var journey = new[]
        {
            Membership(MembershipStatus.Active, renewalDueSoon: false),
            Membership(MembershipStatus.Active, renewalDueSoon: true),
            Membership(MembershipStatus.Active, renewalDueSoon: true),
            Membership(MembershipStatus.GracePeriod),
            Membership(MembershipStatus.GracePeriod),
            Membership(MembershipStatus.Expired),
            Membership(MembershipStatus.Expired),
        };

        var messages = 0;

        foreach (var snapshot in journey)
        {
            var stage = ExpiryNoticeRules.StageFor(ExpiryNoticeRules.EvaluateMembership(snapshot));

            if (ExpiryNoticeRules.ShouldNotify(stage, told))
            {
                messages++;
                told = stage;
            }
        }

        // Three messages for seven sweeps: one per stage, not one per sweep.
        Assert.Equal(3, messages);
    }

    [Fact]
    public void A_renewal_re_arms_the_warnings()
    {
        var told = ExpiryNoticeStage.Ended;

        // Renewed: back to healthy. Nothing is said, but the stage drops.
        var afterRenewal = ExpiryNoticeRules.StageFor(
            ExpiryNoticeRules.EvaluateMembership(Membership(MembershipStatus.Active)));

        Assert.False(ExpiryNoticeRules.ShouldNotify(afterRenewal, told));
        told = afterRenewal;

        // And the next countdown warns again, which would not happen without the reset.
        var nextCycle = ExpiryNoticeRules.StageFor(
            ExpiryNoticeRules.EvaluateMembership(
                Membership(MembershipStatus.Active, renewalDueSoon: true)));

        Assert.True(ExpiryNoticeRules.ShouldNotify(nextCycle, told));
    }

    // -------------------------------------------------------------------- subscriptions ----

    [Fact]
    public void A_subscription_nearing_its_end_date_raises_a_warning()
    {
        var reason = ExpiryNoticeRules.EvaluateSubscription(
            Now.AddDays(3), 1000, 100, Now, warningDays: 7);

        Assert.Equal(ExpiryNoticeReason.SubscriptionExpiringSoon, reason);
    }

    [Fact]
    public void A_subscription_beyond_the_warning_window_raises_nothing()
    {
        var reason = ExpiryNoticeRules.EvaluateSubscription(
            Now.AddDays(30), 1000, 100, Now, warningDays: 7);

        Assert.Equal(ExpiryNoticeReason.None, reason);
    }

    [Fact]
    public void An_expired_subscription_outranks_everything_else()
    {
        // Expired and out of data at once: one problem, one message.
        var reason = ExpiryNoticeRules.EvaluateSubscription(
            Now.AddDays(-1), 1000, 1000, Now, warningDays: 7);

        Assert.Equal(ExpiryNoticeReason.SubscriptionExpired, reason);
    }

    [Fact]
    public void An_exhausted_quota_outranks_an_approaching_date()
    {
        var reason = ExpiryNoticeRules.EvaluateSubscription(
            Now.AddDays(2), 1000, 1000, Now, warningDays: 7);

        Assert.Equal(ExpiryNoticeReason.SubscriptionQuotaExhausted, reason);
    }

    [Theory]
    [InlineData(850, ExpiryNoticeReason.SubscriptionQuotaLow)]
    [InlineData(900, ExpiryNoticeReason.SubscriptionQuotaLow)]
    [InlineData(840, ExpiryNoticeReason.None)]
    public void The_quota_warning_fires_at_its_threshold(long used, ExpiryNoticeReason expected)
    {
        var reason = ExpiryNoticeRules.EvaluateSubscription(
            expiresAt: null, totalBytes: 1000, usedBytes: used, Now,
            warningDays: 7, quotaWarningPercent: 85);

        Assert.Equal(expected, reason);
    }

    [Fact]
    public void An_unlimited_subscription_never_reports_a_quota_problem()
    {
        // total=0 means unlimited on every panel that reports one.
        var reason = ExpiryNoticeRules.EvaluateSubscription(
            expiresAt: null, totalBytes: 0, usedBytes: 999_999, Now, warningDays: 7);

        Assert.Equal(ExpiryNoticeReason.None, reason);
    }

    [Fact]
    public void A_subscription_with_neither_quota_nor_expiry_raises_nothing()
    {
        var reason = ExpiryNoticeRules.EvaluateSubscription(
            expiresAt: null, totalBytes: null, usedBytes: null, Now, warningDays: 7);

        Assert.Equal(ExpiryNoticeReason.None, reason);
    }

    // -------------------------------------------------------------------- presentation ----

    [Theory]
    [InlineData(ExpiryNoticeReason.MembershipRenewalDue, "/Membership")]
    [InlineData(ExpiryNoticeReason.MembershipExpired, "/Membership")]
    [InlineData(ExpiryNoticeReason.SubscriptionExpiringSoon, "/Configs")]
    [InlineData(ExpiryNoticeReason.SubscriptionQuotaExhausted, "/Configs")]
    public void Each_reason_links_somewhere_the_member_can_act(
        ExpiryNoticeReason reason,
        string expected)
    {
        var link = ExpiryNoticeRules.LinkPath(reason);

        Assert.Equal(expected, link);

        // And it has to survive the notification link guard.
        Assert.True(NotificationLinkPolicy.IsAllowed(link));
    }

    [Fact]
    public void Every_reason_has_a_distinct_pair_of_message_keys()
    {
        // Guards against two reasons quietly sharing one message.
        var reasons = Enum.GetValues<ExpiryNoticeReason>()
            .Where(r => r != ExpiryNoticeReason.None)
            .ToList();

        var titles = reasons.Select(ExpiryNoticeRules.TitleKey).ToHashSet();
        var bodies = reasons.Select(ExpiryNoticeRules.BodyKey).ToHashSet();

        Assert.Equal(reasons.Count, titles.Count);
        Assert.Equal(reasons.Count, bodies.Count);
    }

    [Fact]
    public void Every_reason_maps_to_a_stage_above_none()
    {
        foreach (var reason in Enum.GetValues<ExpiryNoticeReason>().Where(r => r != ExpiryNoticeReason.None))
        {
            Assert.True(
                ExpiryNoticeRules.StageFor(reason) > ExpiryNoticeStage.None,
                $"{reason} maps to None, so it would never produce a message.");
        }
    }
}
