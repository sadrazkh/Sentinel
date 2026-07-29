using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Notifications;

namespace Sentinel.Domain.Memberships;

/// <summary>
/// A user's current subscription. One row per user (enforced by a unique index):
/// renewals mutate this row and every change is written to the audit log, which keeps
/// "is this member currently valid?" a single-row lookup on the hot path.
/// </summary>
public class Membership : IConcurrencyAware, ITimestamped
{
    public const int NotesMaxLength = 1024;

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public MembershipTier Tier { get; set; } = MembershipTier.Basic;

    public MembershipAdminState AdminState { get; set; } = MembershipAdminState.Pending;

    public DateTimeOffset StartsAt { get; set; }

    /// <summary><c>null</c> means an open-ended membership that never expires.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>
    /// Per-member override of <c>MembershipOptions.GracePeriodDays</c>.
    /// <c>null</c> falls back to the global setting.
    /// </summary>
    public int? GracePeriodDaysOverride { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// How far along its expiry this membership was when its owner was last warned. The
    /// recurring notifier only speaks when the current stage is higher, which is what keeps a
    /// nightly sweep from repeating the same message. A renewal moves the stage back down and
    /// re-arms the warnings for the next cycle.
    /// </summary>
    public ExpiryNoticeStage LastNoticeStage { get; set; } = ExpiryNoticeStage.None;

    public DateTimeOffset? LastNoticeAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
