using Microsoft.Extensions.Options;
using Sentinel.Application.Options;
using Sentinel.Domain.Memberships;

namespace Sentinel.Application.Memberships;

public interface IMembershipStatusResolver
{
    MembershipSnapshot Resolve(MembershipFacts? facts, DateTimeOffset now);
}

/// <summary>
/// The single place that turns stored membership dates into an effective status.
/// <para>
/// Nothing persists <see cref="MembershipStatus"/>: an "Expired" column would need a
/// scheduled job to stay truthful and would be silently wrong between runs. Deriving it on
/// read means the answer cannot drift, and keeping the derivation here means the rule cannot
/// be reimplemented slightly differently in a second controller.
/// </para>
/// </summary>
public sealed class MembershipStatusResolver : IMembershipStatusResolver
{
    private readonly MembershipOptions _options;

    public MembershipStatusResolver(IOptions<MembershipOptions> options) => _options = options.Value;

    public MembershipSnapshot Resolve(MembershipFacts? facts, DateTimeOffset now)
    {
        if (facts is null)
        {
            return MembershipSnapshot.None;
        }

        // An administrator's explicit decision outranks the calendar: a cancelled or suspended
        // membership stays that way even if its paid window has not run out yet.
        switch (facts.AdminState)
        {
            case MembershipAdminState.Cancelled:
                return Terminal(MembershipStatus.Cancelled, facts);

            case MembershipAdminState.Suspended:
                return Terminal(MembershipStatus.Suspended, facts);

            case MembershipAdminState.Pending:
                return Terminal(MembershipStatus.Pending, facts);
        }

        if (now < facts.StartsAt)
        {
            // Approved, but its window has not opened yet.
            return Terminal(MembershipStatus.Pending, facts);
        }

        if (facts.EndsAt is not { } endsAt)
        {
            // Open-ended membership: active with nothing to count down to.
            return new MembershipSnapshot(
                MembershipStatus.Active, facts.Tier, facts.StartsAt, null, null, null, false);
        }

        var graceDays = Math.Max(0, facts.GracePeriodDaysOverride ?? _options.GracePeriodDays);
        var accessEndsAt = endsAt.AddDays(graceDays);

        if (now <= endsAt)
        {
            return Build(MembershipStatus.Active, facts, endsAt, accessEndsAt, now);
        }

        if (now <= accessEndsAt)
        {
            // Past the paid window but inside the grace period. Access continues on purpose,
            // so a late renewal does not lock a paying customer out mid-task.
            return Build(MembershipStatus.GracePeriod, facts, endsAt, accessEndsAt, now);
        }

        return new MembershipSnapshot(
            MembershipStatus.Expired, facts.Tier, facts.StartsAt, endsAt, accessEndsAt, 0, false);
    }

    private MembershipSnapshot Build(
        MembershipStatus status,
        MembershipFacts facts,
        DateTimeOffset endsAt,
        DateTimeOffset accessEndsAt,
        DateTimeOffset now)
    {
        // Rounded up: with eleven hours left the honest answer is "1 day", not "0".
        var remaining = Math.Max(0, (int)Math.Ceiling((accessEndsAt - now).TotalDays));

        var isRenewalDueSoon = remaining <= _options.RenewalWarningDays;

        return new MembershipSnapshot(
            status, facts.Tier, facts.StartsAt, endsAt, accessEndsAt, remaining, isRenewalDueSoon);
    }

    /// <summary>A status decided without reference to the countdown; dates are still reported.</summary>
    private static MembershipSnapshot Terminal(MembershipStatus status, MembershipFacts facts) =>
        new(status, facts.Tier, facts.StartsAt, facts.EndsAt, null, null, false);
}
