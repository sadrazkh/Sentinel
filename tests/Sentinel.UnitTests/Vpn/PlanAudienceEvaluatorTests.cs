using Sentinel.Domain.Memberships;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Plans;

namespace Sentinel.UnitTests.Vpn;

/// <summary>
/// Who a plan is offered to. The guarantee that matters is that an explicit deny cannot be
/// overturned by any combination of allows — an operator excluding one account must not have to
/// audit every other rule to be sure it took effect.
/// </summary>
public sealed class PlanAudienceEvaluatorTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static AudienceSubject Member(
        MembershipTier? tier = MembershipTier.Pro,
        params string[] roles) =>
        AudienceSubject.For(Alice, tier, roles);

    private static AudienceRuleFacts Allow(
        AudienceRuleKind kind,
        MembershipTier? tier = null,
        string? role = null,
        Guid? userId = null) =>
        new(AudienceEffect.Allow, kind, tier, role, userId);

    private static AudienceRuleFacts Deny(
        AudienceRuleKind kind,
        MembershipTier? tier = null,
        string? role = null,
        Guid? userId = null) =>
        new(AudienceEffect.Deny, kind, tier, role, userId);

    // ------------------------------------------------------------------------- defaults ----

    [Fact]
    public void A_plan_with_no_rules_is_open_to_anyone()
    {
        // A plan nobody has restricted is not a restricted plan.
        Assert.Equal(AudienceDecision.AllowedByDefault, PlanAudienceEvaluator.Evaluate(Member(), []));
    }

    [Fact]
    public void A_plan_with_no_rules_is_open_even_to_a_member_with_no_membership()
    {
        Assert.True(PlanAudienceEvaluator.IsInAudience(Member(tier: null), []));
    }

    // ------------------------------------------------------------------ deny beats allow ----

    [Fact]
    public void A_matching_deny_beats_a_matching_allow()
    {
        var rules = new[]
        {
            Allow(AudienceRuleKind.Everyone),
            Deny(AudienceRuleKind.User, userId: Alice),
        };

        Assert.Equal(AudienceDecision.DeniedByRule, PlanAudienceEvaluator.Evaluate(Member(), rules));
    }

    [Fact]
    public void A_deny_wins_regardless_of_where_it_sits_in_the_list()
    {
        // Order-independence is the whole point. If the answer depended on rule order, an operator
        // would have to reason about a list they cannot see the order of.
        var deny = Deny(AudienceRuleKind.User, userId: Alice);
        var allows = new[]
        {
            Allow(AudienceRuleKind.Everyone),
            Allow(AudienceRuleKind.MinimumTier, tier: MembershipTier.Basic),
            Allow(AudienceRuleKind.User, userId: Alice),
        };

        for (var position = 0; position <= allows.Length; position++)
        {
            var rules = allows.Take(position).Append(deny).Concat(allows.Skip(position)).ToList();

            Assert.Equal(
                AudienceDecision.DeniedByRule,
                PlanAudienceEvaluator.Evaluate(Member(), rules));
        }
    }

    [Fact]
    public void A_deny_wins_against_every_number_of_allows()
    {
        var rules = new List<AudienceRuleFacts> { Deny(AudienceRuleKind.Everyone) };

        for (var count = 0; count < 20; count++)
        {
            rules.Add(Allow(AudienceRuleKind.User, userId: Alice));

            Assert.False(PlanAudienceEvaluator.IsInAudience(Member(), rules));
        }
    }

    [Fact]
    public void A_deny_that_does_not_match_leaves_the_allow_standing()
    {
        // Bob's exclusion says nothing about Alice.
        var rules = new[]
        {
            Allow(AudienceRuleKind.Everyone),
            Deny(AudienceRuleKind.User, userId: Bob),
        };

        Assert.Equal(AudienceDecision.AllowedByRule, PlanAudienceEvaluator.Evaluate(Member(), rules));
    }

    [Fact]
    public void A_deny_on_its_own_closes_a_plan_that_would_otherwise_be_open()
    {
        var rules = new[] { Deny(AudienceRuleKind.Everyone) };

        Assert.Equal(AudienceDecision.DeniedByRule, PlanAudienceEvaluator.Evaluate(Member(), rules));
    }

    [Fact]
    public void Everyone_except_one_account_is_expressible()
    {
        // The pattern the two effects exist to support.
        var rules = new[]
        {
            Allow(AudienceRuleKind.Everyone),
            Deny(AudienceRuleKind.User, userId: Bob),
        };

        Assert.True(PlanAudienceEvaluator.IsInAudience(AudienceSubject.For(Alice, MembershipTier.Basic, []), rules));
        Assert.False(PlanAudienceEvaluator.IsInAudience(AudienceSubject.For(Bob, MembershipTier.Elite, []), rules));
    }

    // ------------------------------------------------------------------ allow as a gate ----

    [Fact]
    public void A_plan_that_lists_an_audience_is_closed_to_everyone_outside_it()
    {
        var rules = new[] { Allow(AudienceRuleKind.MembershipTier, tier: MembershipTier.Elite) };

        Assert.Equal(
            AudienceDecision.DeniedByOmission,
            PlanAudienceEvaluator.Evaluate(Member(MembershipTier.Pro), rules));

        Assert.Equal(
            AudienceDecision.AllowedByRule,
            PlanAudienceEvaluator.Evaluate(Member(MembershipTier.Elite), rules));
    }

    [Fact]
    public void Matching_any_one_allow_is_enough()
    {
        // Allows are alternatives, not requirements. Needing all of them would make two audiences
        // impossible to express.
        var rules = new[]
        {
            Allow(AudienceRuleKind.MembershipTier, tier: MembershipTier.Elite),
            Allow(AudienceRuleKind.Role, role: "Support"),
        };

        Assert.True(PlanAudienceEvaluator.IsInAudience(Member(MembershipTier.Basic, "Support"), rules));
    }

    // ------------------------------------------------------------------------- matching ----

    [Theory]
    [InlineData(MembershipTier.Basic, false)]
    [InlineData(MembershipTier.Pro, true)]
    [InlineData(MembershipTier.Elite, false)]
    public void An_exact_tier_rule_matches_only_that_tier(MembershipTier held, bool expected) =>
        Assert.Equal(
            expected,
            PlanAudienceEvaluator.IsInAudience(
                Member(held),
                [Allow(AudienceRuleKind.MembershipTier, tier: MembershipTier.Pro)]));

    [Theory]
    [InlineData(MembershipTier.Basic, false)]
    [InlineData(MembershipTier.Pro, true)]
    [InlineData(MembershipTier.Elite, true)]
    public void A_minimum_tier_rule_matches_upwards(MembershipTier held, bool expected) =>
        Assert.Equal(
            expected,
            PlanAudienceEvaluator.IsInAudience(
                Member(held),
                [Allow(AudienceRuleKind.MinimumTier, tier: MembershipTier.Pro)]));

    [Fact]
    public void A_member_with_no_membership_matches_no_tier_rule()
    {
        // Correct for both effects: the rule is about a tier they are not in. A deny on Basic must
        // not catch somebody who has no tier at all.
        Assert.Equal(
            AudienceDecision.DeniedByOmission,
            PlanAudienceEvaluator.Evaluate(
                Member(tier: null),
                [Allow(AudienceRuleKind.MinimumTier, tier: MembershipTier.Basic)]));

        Assert.Equal(
            AudienceDecision.AllowedByRule,
            PlanAudienceEvaluator.Evaluate(
                Member(tier: null),
                [
                    Allow(AudienceRuleKind.Everyone),
                    Deny(AudienceRuleKind.MembershipTier, tier: MembershipTier.Basic),
                ]));
    }

    [Fact]
    public void A_role_rule_is_case_insensitive()
    {
        // Role names arrive from Identity and from an operator's typing; a case difference is not
        // a different role.
        Assert.True(PlanAudienceEvaluator.IsInAudience(
            Member(roles: "support"),
            [Allow(AudienceRuleKind.Role, role: "Support")]));
    }

    [Fact]
    public void A_role_rule_with_no_role_name_matches_nothing()
    {
        // A half-filled rule must not become a wildcard.
        Assert.Equal(
            AudienceDecision.DeniedByOmission,
            PlanAudienceEvaluator.Evaluate(Member(roles: "Support"), [Allow(AudienceRuleKind.Role)]));
    }

    [Fact]
    public void A_tier_rule_with_no_tier_matches_nothing() =>
        Assert.Equal(
            AudienceDecision.DeniedByOmission,
            PlanAudienceEvaluator.Evaluate(Member(), [Allow(AudienceRuleKind.MembershipTier)]));

    [Fact]
    public void A_user_rule_with_no_user_matches_nothing() =>
        Assert.Equal(
            AudienceDecision.DeniedByOmission,
            PlanAudienceEvaluator.Evaluate(Member(), [Allow(AudienceRuleKind.User)]));

    [Fact]
    public void An_unrecognised_rule_kind_withholds_rather_than_opens()
    {
        // A new enum member added without a branch in the matcher must fail closed.
        var rules = new[] { new AudienceRuleFacts(AudienceEffect.Allow, (AudienceRuleKind)999, null, null, null) };

        Assert.Equal(AudienceDecision.DeniedByOmission, PlanAudienceEvaluator.Evaluate(Member(), rules));
    }

    [Fact]
    public void An_unrecognised_deny_kind_does_not_accidentally_deny_everyone()
    {
        var rules = new[]
        {
            Allow(AudienceRuleKind.Everyone),
            new AudienceRuleFacts(AudienceEffect.Deny, (AudienceRuleKind)999, null, null, null),
        };

        Assert.True(PlanAudienceEvaluator.IsInAudience(Member(), rules));
    }

    // ---------------------------------------------------------------------- exhaustive ----

    [Fact]
    public void A_matching_deny_denies_across_every_combination_of_rules()
    {
        // Brute force over the whole rule vocabulary: whenever a deny matches, the answer is deny.
        var subject = AudienceSubject.For(Alice, MembershipTier.Pro, ["Member", "Support"]);

        var candidates = new[]
        {
            Allow(AudienceRuleKind.Everyone),
            Allow(AudienceRuleKind.MembershipTier, tier: MembershipTier.Pro),
            Allow(AudienceRuleKind.MinimumTier, tier: MembershipTier.Basic),
            Allow(AudienceRuleKind.Role, role: "Support"),
            Allow(AudienceRuleKind.User, userId: Alice),
        };

        var denies = new[]
        {
            Deny(AudienceRuleKind.Everyone),
            Deny(AudienceRuleKind.MembershipTier, tier: MembershipTier.Pro),
            Deny(AudienceRuleKind.MinimumTier, tier: MembershipTier.Basic),
            Deny(AudienceRuleKind.Role, role: "Support"),
            Deny(AudienceRuleKind.User, userId: Alice),
        };

        // Every subset of the allows, times every deny.
        for (var mask = 0; mask < 1 << candidates.Length; mask++)
        {
            var allows = candidates
                .Where((_, index) => (mask & (1 << index)) != 0)
                .ToList();

            foreach (var deny in denies)
            {
                var rules = allows.Append(deny).ToList();

                Assert.Equal(
                    AudienceDecision.DeniedByRule,
                    PlanAudienceEvaluator.Evaluate(subject, rules));
            }
        }
    }
}
