using Sentinel.Domain.Memberships;
using Sentinel.Vpn.Domain;

namespace Sentinel.Vpn.Plans;

/// <summary>Who is asking. Everything the audience rules can match on, and nothing else.</summary>
public sealed record AudienceSubject(
    Guid UserId,
    MembershipTier? Tier,
    IReadOnlySet<string> Roles)
{
    public static AudienceSubject For(Guid userId, MembershipTier? tier, IEnumerable<string>? roles) =>
        new(userId, tier, (roles ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase));
}

/// <summary>One rule reduced to what the evaluator needs, so it can be tested without EF.</summary>
public sealed record AudienceRuleFacts(
    AudienceEffect Effect,
    AudienceRuleKind Kind,
    MembershipTier? Tier,
    string? RoleName,
    Guid? UserId)
{
    public static AudienceRuleFacts From(PlanAudienceRule rule) => new(
        rule.Effect, rule.Kind, rule.Tier, rule.RoleName, rule.UserId);
}

/// <summary>Why a plan was withheld, for the operator's own diagnostics — never shown to a member.</summary>
public enum AudienceDecision
{
    /// <summary>No rules at all: the plan is for anyone who can see the product.</summary>
    AllowedByDefault = 0,

    AllowedByRule = 1,

    /// <summary>An explicit deny matched. Beats every allow.</summary>
    DeniedByRule = 2,

    /// <summary>The plan has allow rules and this member matched none of them.</summary>
    DeniedByOmission = 3,
}

/// <summary>
/// Decides whether one member is in a plan's audience.
/// <para>
/// The rule is stated once, here, and it is deliberately the least surprising one:
/// </para>
/// <list type="number">
/// <item><b>Any matching deny wins.</b> Checked first and returns immediately, so no combination of
/// allows can ever overturn it. That is what makes a deny usable as a safety measure — an operator
/// excluding one account does not have to audit every other rule to be sure it took effect.</item>
/// <item>Otherwise, if the plan has <em>any</em> allow rules, the member must match one. A plan that
/// lists an audience is closed to everyone outside it.</item>
/// <item>Otherwise the plan is open. A plan with no rules is a plan nobody has restricted.</item>
/// </list>
/// <para>
/// A pure function over its inputs, so every combination can be tested without a database — which
/// matters because this decides what a customer is offered.
/// </para>
/// </summary>
public static class PlanAudienceEvaluator
{
    public static AudienceDecision Evaluate(
        AudienceSubject subject,
        IReadOnlyList<AudienceRuleFacts> rules)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(rules);

        var hasAllow = false;
        var matchedAllow = false;

        foreach (var rule in rules)
        {
            var matches = Matches(subject, rule);

            if (rule.Effect == AudienceEffect.Deny)
            {
                if (matches)
                {
                    // Returns straight away. Continuing to look for an allow would be the bug this
                    // ordering exists to prevent.
                    return AudienceDecision.DeniedByRule;
                }

                continue;
            }

            hasAllow = true;
            matchedAllow |= matches;
        }

        if (!hasAllow)
        {
            return AudienceDecision.AllowedByDefault;
        }

        return matchedAllow ? AudienceDecision.AllowedByRule : AudienceDecision.DeniedByOmission;
    }

    public static bool IsInAudience(AudienceSubject subject, IReadOnlyList<AudienceRuleFacts> rules) =>
        Evaluate(subject, rules) is AudienceDecision.AllowedByDefault or AudienceDecision.AllowedByRule;

    private static bool Matches(AudienceSubject subject, AudienceRuleFacts rule) => rule.Kind switch
    {
        AudienceRuleKind.Everyone => true,

        // A member with no membership matches no tier rule — not even a deny. That is correct for
        // both effects: the rule is about a tier they are not in.
        AudienceRuleKind.MembershipTier =>
            rule.Tier is { } tier && subject.Tier == tier,

        AudienceRuleKind.MinimumTier =>
            rule.Tier is { } minimum && subject.Tier is { } held && held >= minimum,

        AudienceRuleKind.Role =>
            !string.IsNullOrWhiteSpace(rule.RoleName) && subject.Roles.Contains(rule.RoleName),

        AudienceRuleKind.User =>
            rule.UserId is { } userId && subject.UserId == userId,

        // An unrecognised kind matches nothing. A new enum member added without a branch here
        // therefore withholds a plan rather than opening one to everybody.
        _ => false,
    };
}
