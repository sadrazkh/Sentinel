using Sentinel.Application.Access;
using Sentinel.Application.Memberships;
using Sentinel.Domain.Catalog;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Memberships;

namespace Sentinel.UnitTests.Access;

public sealed class AccessRuleEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static AccountFacts ActiveAccount => new(UserAccountStatus.Active, null);

    private static MembershipSnapshot ActiveMembership(MembershipTier tier = MembershipTier.Pro) =>
        new(MembershipStatus.Active, tier, Now.AddDays(-30), Now.AddDays(30), Now.AddDays(30), 30, false);

    private static MembershipSnapshot ExpiredMembership() =>
        new(MembershipStatus.Expired, MembershipTier.Pro, Now.AddDays(-90), Now.AddDays(-10),
            Now.AddDays(-7), 0, false);

    private static ApplicationFacts App(
        bool isEnabled = true,
        ApplicationPublishStatus publishStatus = ApplicationPublishStatus.Published,
        bool requiresEntitlement = false,
        MembershipTier? minimumTier = null) =>
        new(Guid.NewGuid(), "demo", isEnabled, publishStatus, requiresEntitlement, minimumTier);

    private static EntitlementFacts Grant(
        bool isEnabled = true,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null) =>
        new(isEnabled, startsAt ?? Now.AddDays(-1), expiresAt, revokedAt);

    private static AccessDecision Evaluate(
        ApplicationFacts application,
        MembershipSnapshot? membership = null,
        EntitlementFacts? entitlement = null,
        AccountFacts? account = null) =>
        AccessRuleEvaluator.Evaluate(new AccessContext(
            account ?? ActiveAccount,
            membership ?? ActiveMembership(),
            application,
            entitlement,
            Now));

    // ------------------------------------------------------------------------- the account

    [Fact]
    public void A_disabled_account_is_refused_before_anything_else_is_considered()
    {
        var decision = Evaluate(App(), entitlement: Grant(), account: new(UserAccountStatus.Disabled, null));

        Assert.False(decision.IsAllowed);
        Assert.Equal(AccessDenialReason.AccountDisabled, decision.Reason);
    }

    [Fact]
    public void An_open_ended_suspension_refuses_access()
    {
        var decision = Evaluate(App(), account: new(UserAccountStatus.Suspended, null));

        Assert.Equal(AccessDenialReason.AccountSuspended, decision.Reason);
    }

    [Fact]
    public void A_suspension_that_has_run_out_no_longer_blocks_access()
    {
        var decision = Evaluate(App(), account: new(UserAccountStatus.Suspended, Now.AddHours(-1)));

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void A_suspension_still_in_the_future_blocks_access()
    {
        var decision = Evaluate(App(), account: new(UserAccountStatus.Suspended, Now.AddHours(1)));

        Assert.Equal(AccessDenialReason.AccountSuspended, decision.Reason);
    }

    [Fact]
    public void The_account_is_checked_before_the_application()
    {
        // Both would fail; the account reason must be the one reported.
        var decision = Evaluate(
            App(publishStatus: ApplicationPublishStatus.ComingSoon),
            account: new(UserAccountStatus.Disabled, null));

        Assert.Equal(AccessDenialReason.AccountDisabled, decision.Reason);
    }

    // --------------------------------------------------------------------- the application

    [Fact]
    public void A_disabled_application_is_closed_even_to_a_holder_of_an_explicit_grant()
    {
        // The master switch has to mean something, or turning an application off would not
        // actually take it out of service.
        var decision = Evaluate(App(isEnabled: false), entitlement: Grant());

        Assert.Equal(AccessDenialReason.ApplicationDisabled, decision.Reason);
    }

    [Theory]
    [InlineData(ApplicationPublishStatus.Draft, AccessDenialReason.ApplicationNotPublished)]
    [InlineData(ApplicationPublishStatus.ComingSoon, AccessDenialReason.ApplicationComingSoon)]
    [InlineData(ApplicationPublishStatus.Retired, AccessDenialReason.ApplicationRetired)]
    public void Publish_status_governs_launchability(
        ApplicationPublishStatus status,
        AccessDenialReason expected)
    {
        var decision = Evaluate(App(publishStatus: status), entitlement: Grant());

        Assert.Equal(expected, decision.Reason);
    }

    // ------------------------------------------------------------------ the membership path

    [Fact]
    public void A_valid_membership_opens_an_ordinary_application()
    {
        Assert.True(Evaluate(App()).IsAllowed);
    }

    [Fact]
    public void An_expired_membership_closes_an_ordinary_application()
    {
        var decision = Evaluate(App(), membership: ExpiredMembership());

        Assert.Equal(AccessDenialReason.MembershipInvalid, decision.Reason);
    }

    [Fact]
    public void A_membership_in_its_grace_period_still_opens_applications()
    {
        var grace = new MembershipSnapshot(
            MembershipStatus.GracePeriod, MembershipTier.Pro,
            Now.AddDays(-90), Now.AddDays(-1), Now.AddDays(2), 2, true);

        Assert.True(Evaluate(App(), membership: grace).IsAllowed);
    }

    [Fact]
    public void Having_no_membership_at_all_closes_an_ordinary_application()
    {
        var decision = Evaluate(App(), membership: MembershipSnapshot.None);

        Assert.Equal(AccessDenialReason.MembershipInvalid, decision.Reason);
    }

    [Theory]
    [InlineData(MembershipTier.Basic, MembershipTier.Pro, false)]
    [InlineData(MembershipTier.Pro, MembershipTier.Pro, true)]
    [InlineData(MembershipTier.Elite, MembershipTier.Pro, true)]
    public void The_minimum_tier_is_an_inclusive_floor(
        MembershipTier held,
        MembershipTier required,
        bool expectedAllowed)
    {
        var decision = Evaluate(App(minimumTier: required), membership: ActiveMembership(held));

        Assert.Equal(expectedAllowed, decision.IsAllowed);

        if (!expectedAllowed)
        {
            Assert.Equal(AccessDenialReason.TierTooLow, decision.Reason);
        }
    }

    // ----------------------------------------------------------------- the entitlement path

    [Fact]
    public void A_usable_grant_opens_an_application_despite_an_expired_membership()
    {
        // This is the point of entitlements: an individual arrangement that does not depend
        // on the subscription being live.
        var decision = Evaluate(App(), membership: ExpiredMembership(), entitlement: Grant());

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void A_usable_grant_overrides_a_tier_the_member_does_not_hold()
    {
        var decision = Evaluate(
            App(minimumTier: MembershipTier.Elite),
            membership: ActiveMembership(MembershipTier.Basic),
            entitlement: Grant());

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void An_application_that_requires_a_grant_is_not_opened_by_membership_alone()
    {
        var decision = Evaluate(App(requiresEntitlement: true));

        Assert.Equal(AccessDenialReason.NoEntitlement, decision.Reason);
    }

    [Theory]
    [InlineData(false, null, null, AccessDenialReason.EntitlementDisabled)]
    [InlineData(true, -10, null, AccessDenialReason.EntitlementRevoked)]
    public void An_unusable_grant_on_a_restricted_application_reports_why(
        bool isEnabled,
        int? revokedDaysAgo,
        int? expiredDaysAgo,
        AccessDenialReason expected)
    {
        var entitlement = Grant(
            isEnabled: isEnabled,
            expiresAt: expiredDaysAgo is { } e ? Now.AddDays(e) : null,
            revokedAt: revokedDaysAgo is { } r ? Now.AddDays(r) : null);

        var decision = Evaluate(App(requiresEntitlement: true), entitlement: entitlement);

        Assert.Equal(expected, decision.Reason);
    }

    [Fact]
    public void A_grant_that_has_not_started_reports_that_specifically()
    {
        var decision = Evaluate(
            App(requiresEntitlement: true),
            entitlement: Grant(startsAt: Now.AddDays(3)));

        Assert.Equal(AccessDenialReason.EntitlementNotStarted, decision.Reason);
    }

    [Fact]
    public void An_expired_grant_reports_that_specifically()
    {
        var decision = Evaluate(
            App(requiresEntitlement: true),
            entitlement: Grant(expiresAt: Now.AddDays(-1)));

        Assert.Equal(AccessDenialReason.EntitlementExpired, decision.Reason);
    }

    [Fact]
    public void Revocation_wins_over_the_grants_own_dates()
    {
        // A grant revoked today but nominally valid until next year is revoked, full stop.
        var decision = Evaluate(
            App(requiresEntitlement: true),
            entitlement: Grant(expiresAt: Now.AddDays(365), revokedAt: Now.AddMinutes(-1)));

        Assert.Equal(AccessDenialReason.EntitlementRevoked, decision.Reason);
    }

    [Fact]
    public void A_grant_expiring_in_the_future_is_still_usable()
    {
        Assert.True(Evaluate(App(requiresEntitlement: true), entitlement: Grant(expiresAt: Now.AddDays(1)))
            .IsAllowed);
    }

    // --------------------------------------------------------- interaction between the paths

    [Fact]
    public void A_revoked_grant_does_not_close_an_application_membership_already_opens()
    {
        // Revoking an individual grant removes the individual arrangement; it is not a ban.
        // For an application any member may use, the membership must still let them in.
        var decision = Evaluate(App(), entitlement: Grant(revokedAt: Now.AddDays(-1)));

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void An_expired_grant_falls_back_to_the_membership()
    {
        var decision = Evaluate(App(), entitlement: Grant(expiresAt: Now.AddDays(-1)));

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void An_expired_grant_and_an_expired_membership_together_report_the_membership()
    {
        var decision = Evaluate(
            App(),
            membership: ExpiredMembership(),
            entitlement: Grant(expiresAt: Now.AddDays(-1)));

        Assert.Equal(AccessDenialReason.MembershipInvalid, decision.Reason);
    }

    // ------------------------------------------------------------------------ presentation

    [Theory]
    [InlineData(AccessDenialReason.MembershipInvalid, true)]
    [InlineData(AccessDenialReason.TierTooLow, true)]
    [InlineData(AccessDenialReason.ApplicationComingSoon, true)]
    [InlineData(AccessDenialReason.NoEntitlement, true)]
    [InlineData(AccessDenialReason.ApplicationDisabled, false)]
    [InlineData(AccessDenialReason.ApplicationNotPublished, false)]
    [InlineData(AccessDenialReason.AccountDisabled, false)]
    public void Only_reasons_a_member_could_act_on_leave_the_card_visible(
        AccessDenialReason reason,
        bool expectedVisible)
    {
        // A member should see what renewing would unlock, but must not be shown applications
        // that are switched off or unpublished — those are internal states.
        Assert.Equal(expectedVisible, AccessDecision.Denied(reason).IsVisibleButLocked);
    }

    [Fact]
    public void An_allowed_decision_is_never_reported_as_locked()
    {
        Assert.False(AccessDecision.Allowed.IsVisibleButLocked);
    }
}
