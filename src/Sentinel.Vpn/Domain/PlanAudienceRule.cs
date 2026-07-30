using Sentinel.Domain.Common;
using Sentinel.Domain.Memberships;

namespace Sentinel.Vpn.Domain;

/// <summary>Whether a matching rule opens a plan or closes it.</summary>
public enum AudienceEffect
{
    Allow = 0,

    /// <summary>Beats every allow. See <see cref="Plans.PlanAudienceEvaluator"/>.</summary>
    Deny = 1,
}

/// <summary>
/// What a rule matches on.
/// <para>
/// A closed enum, not an expression language. The spec asked for audience rules without a rule
/// engine, and this is the difference: every kind here is a single typed comparison the compiler
/// can check, so a new dimension is a new enum member plus one branch — not a parser, a grammar and
/// a class of runtime failures that only show up in production with a customer's data.
/// </para>
/// </summary>
public enum AudienceRuleKind
{
    /// <summary>Matches every member. The way to write "everyone except…" alongside a deny.</summary>
    Everyone = 0,

    /// <summary>Matches one membership tier exactly.</summary>
    MembershipTier = 1,

    /// <summary>Matches a tier and everything above it.</summary>
    MinimumTier = 2,

    /// <summary>Matches one named role.</summary>
    Role = 3,

    /// <summary>Matches one specific member — for a private plan, or to exclude one account.</summary>
    User = 4,
}

/// <summary>
/// One rule about who a plan is for.
/// <para>
/// Rules are additive rows rather than columns on the plan, so an operator can express "Pro and
/// above, but not this one account" without the plan table growing a field per exception.
/// </para>
/// </summary>
public class PlanAudienceRule : ITimestamped
{
    public const int RoleNameMaxLength = 64;
    public const int NoteMaxLength = 300;

    public Guid Id { get; set; }

    public Guid PlanId { get; set; }

    public ServicePlan? Plan { get; set; }

    public AudienceEffect Effect { get; set; } = AudienceEffect.Allow;

    public AudienceRuleKind Kind { get; set; } = AudienceRuleKind.Everyone;

    /// <summary>Set for <see cref="AudienceRuleKind.MembershipTier"/> and <see cref="AudienceRuleKind.MinimumTier"/>.</summary>
    public MembershipTier? Tier { get; set; }

    /// <summary>Set for <see cref="AudienceRuleKind.Role"/>.</summary>
    public string? RoleName { get; set; }

    /// <summary>Set for <see cref="AudienceRuleKind.User"/>.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Why the rule exists. For the operator, never shown to a member.</summary>
    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
