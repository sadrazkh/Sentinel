using Microsoft.Extensions.Options;
using Sentinel.Application.Memberships;
using Sentinel.Application.Options;
using Sentinel.Domain.Memberships;

namespace Sentinel.UnitTests.Memberships;

public sealed class MembershipStatusResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static MembershipStatusResolver Resolver(int gracePeriodDays = 3, int renewalWarningDays = 7) =>
        new(Options.Create(new MembershipOptions
        {
            GracePeriodDays = gracePeriodDays,
            RenewalWarningDays = renewalWarningDays,
        }));

    private static MembershipFacts Facts(
        MembershipAdminState state = MembershipAdminState.Active,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null,
        MembershipTier tier = MembershipTier.Pro,
        int? graceOverride = null) =>
        new(tier, state, startsAt ?? Now.AddDays(-30), endsAt, graceOverride);

    // --------------------------------------------------------------- absence and admin state

    [Fact]
    public void No_membership_record_resolves_to_None()
    {
        var snapshot = Resolver().Resolve(null, Now);

        Assert.Equal(MembershipStatus.None, snapshot.Status);
        Assert.False(snapshot.GrantsAccess);
        Assert.Null(snapshot.Tier);
    }

    [Theory]
    [InlineData(MembershipAdminState.Cancelled, MembershipStatus.Cancelled)]
    [InlineData(MembershipAdminState.Suspended, MembershipStatus.Suspended)]
    [InlineData(MembershipAdminState.Pending, MembershipStatus.Pending)]
    public void An_administrators_decision_outranks_the_calendar(
        MembershipAdminState state,
        MembershipStatus expected)
    {
        // The date window is wide open; the stored state must still win.
        var facts = Facts(state, startsAt: Now.AddDays(-10), endsAt: Now.AddDays(300));

        var snapshot = Resolver().Resolve(facts, Now);

        Assert.Equal(expected, snapshot.Status);
        Assert.False(snapshot.GrantsAccess);
    }

    [Fact]
    public void A_membership_whose_window_has_not_opened_is_pending()
    {
        var facts = Facts(startsAt: Now.AddDays(1), endsAt: Now.AddDays(30));

        var snapshot = Resolver().Resolve(facts, Now);

        Assert.Equal(MembershipStatus.Pending, snapshot.Status);
        Assert.False(snapshot.GrantsAccess);
    }

    // ------------------------------------------------------------------------ active window

    [Fact]
    public void An_open_ended_membership_is_active_with_nothing_to_count_down()
    {
        var snapshot = Resolver().Resolve(Facts(endsAt: null), Now);

        Assert.Equal(MembershipStatus.Active, snapshot.Status);
        Assert.True(snapshot.GrantsAccess);
        Assert.Null(snapshot.DaysRemaining);
        Assert.Null(snapshot.AccessEndsAt);
        Assert.False(snapshot.IsRenewalDueSoon);
    }

    [Fact]
    public void A_membership_inside_its_window_is_active()
    {
        var snapshot = Resolver().Resolve(Facts(endsAt: Now.AddDays(30)), Now);

        Assert.Equal(MembershipStatus.Active, snapshot.Status);
        Assert.True(snapshot.GrantsAccess);
        Assert.Equal(MembershipTier.Pro, snapshot.Tier);
    }

    [Fact]
    public void The_final_instant_of_the_window_is_still_active()
    {
        // Boundary: "ends at" means access through that instant, not up to the day before.
        var snapshot = Resolver().Resolve(Facts(endsAt: Now), Now);

        Assert.Equal(MembershipStatus.Active, snapshot.Status);
    }

    // ------------------------------------------------------------------------ grace period

    [Fact]
    public void Just_past_the_end_date_falls_into_the_grace_period()
    {
        var snapshot = Resolver(gracePeriodDays: 3).Resolve(Facts(endsAt: Now.AddSeconds(-1)), Now);

        Assert.Equal(MembershipStatus.GracePeriod, snapshot.Status);

        // Grace still grants access — that is the entire reason it exists.
        Assert.True(snapshot.GrantsAccess);
        Assert.True(snapshot.IsInGracePeriod);
    }

    [Fact]
    public void Past_the_grace_period_the_membership_is_expired()
    {
        var snapshot = Resolver(gracePeriodDays: 3).Resolve(Facts(endsAt: Now.AddDays(-4)), Now);

        Assert.Equal(MembershipStatus.Expired, snapshot.Status);
        Assert.False(snapshot.GrantsAccess);
        Assert.Equal(0, snapshot.DaysRemaining);
    }

    [Fact]
    public void The_final_instant_of_the_grace_period_still_grants_access()
    {
        var endsAt = Now.AddDays(-3);

        var snapshot = Resolver(gracePeriodDays: 3).Resolve(Facts(endsAt: endsAt), Now);

        Assert.Equal(MembershipStatus.GracePeriod, snapshot.Status);
        Assert.True(snapshot.GrantsAccess);
    }

    [Fact]
    public void A_zero_day_grace_period_expires_the_moment_the_window_closes()
    {
        var snapshot = Resolver(gracePeriodDays: 0).Resolve(Facts(endsAt: Now.AddSeconds(-1)), Now);

        Assert.Equal(MembershipStatus.Expired, snapshot.Status);
    }

    [Fact]
    public void A_per_member_override_replaces_the_global_grace_period()
    {
        // Global grace would have expired this membership; the override keeps it alive.
        var facts = Facts(endsAt: Now.AddDays(-5), graceOverride: 10);

        var snapshot = Resolver(gracePeriodDays: 1).Resolve(facts, Now);

        Assert.Equal(MembershipStatus.GracePeriod, snapshot.Status);
    }

    [Fact]
    public void A_negative_override_is_clamped_rather_than_shortening_the_window()
    {
        // A bad value must not silently move AccessEndsAt earlier than EndsAt.
        var endsAt = Now.AddSeconds(-1);
        var facts = Facts(endsAt: endsAt, graceOverride: -30);

        var snapshot = Resolver(gracePeriodDays: 5).Resolve(facts, Now);

        Assert.Equal(MembershipStatus.Expired, snapshot.Status);
        Assert.Equal(endsAt, snapshot.AccessEndsAt);
    }

    // ------------------------------------------------------------------ countdown and warning

    [Fact]
    public void Days_remaining_round_up_so_a_partial_day_never_reads_as_zero()
    {
        // Eleven hours left is "1 day", not "0 days".
        var snapshot = Resolver(gracePeriodDays: 0).Resolve(Facts(endsAt: Now.AddHours(11)), Now);

        Assert.Equal(1, snapshot.DaysRemaining);
    }

    [Fact]
    public void Days_remaining_counts_to_the_end_of_the_grace_period_not_the_end_date()
    {
        // What a member cares about is when access actually stops.
        var snapshot = Resolver(gracePeriodDays: 5).Resolve(Facts(endsAt: Now.AddDays(10)), Now);

        Assert.Equal(15, snapshot.DaysRemaining);
    }

    [Fact]
    public void The_renewal_warning_fires_inside_the_configured_window()
    {
        var snapshot = Resolver(gracePeriodDays: 0, renewalWarningDays: 7)
            .Resolve(Facts(endsAt: Now.AddDays(5)), Now);

        Assert.True(snapshot.IsRenewalDueSoon);
    }

    [Fact]
    public void The_renewal_warning_stays_quiet_outside_the_window()
    {
        var snapshot = Resolver(gracePeriodDays: 0, renewalWarningDays: 7)
            .Resolve(Facts(endsAt: Now.AddDays(40)), Now);

        Assert.False(snapshot.IsRenewalDueSoon);
    }

    [Fact]
    public void An_expired_membership_does_not_nag_about_renewal_dates()
    {
        // Expired is its own, louder message; a countdown warning on top would be noise.
        var snapshot = Resolver(gracePeriodDays: 0).Resolve(Facts(endsAt: Now.AddDays(-40)), Now);

        Assert.Equal(MembershipStatus.Expired, snapshot.Status);
        Assert.False(snapshot.IsRenewalDueSoon);
    }

    [Fact]
    public void Dates_are_still_reported_for_a_terminal_status()
    {
        // The membership page shows the dates even when the status was set by an admin.
        var startsAt = Now.AddDays(-100);
        var endsAt = Now.AddDays(100);

        var snapshot = Resolver().Resolve(
            Facts(MembershipAdminState.Suspended, startsAt, endsAt), Now);

        Assert.Equal(startsAt, snapshot.StartsAt);
        Assert.Equal(endsAt, snapshot.EndsAt);
        Assert.Equal(MembershipTier.Pro, snapshot.Tier);
    }

    [Theory]
    [InlineData(MembershipStatus.Active, true)]
    [InlineData(MembershipStatus.GracePeriod, true)]
    [InlineData(MembershipStatus.None, false)]
    [InlineData(MembershipStatus.Pending, false)]
    [InlineData(MembershipStatus.Expired, false)]
    [InlineData(MembershipStatus.Suspended, false)]
    [InlineData(MembershipStatus.Cancelled, false)]
    public void Only_active_and_grace_period_grant_access(MembershipStatus status, bool expected)
    {
        Assert.Equal(expected, status.GrantsAccess());
    }
}
