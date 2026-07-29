using System.ComponentModel.DataAnnotations;

namespace Sentinel.Application.Options;

public sealed class MembershipOptions
{
    public const string SectionName = "Membership";

    /// <summary>
    /// Days after <c>Membership.EndsAt</c> during which access is still granted, so a late
    /// renewal does not lock a paying customer out. Individual memberships may override it.
    /// </summary>
    [Range(0, 90)]
    public int GracePeriodDays { get; set; }

    /// <summary>How many days before expiry the dashboard starts showing a renewal warning.</summary>
    [Range(0, 180)]
    public int RenewalWarningDays { get; set; } = 7;
}
