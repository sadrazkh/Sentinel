namespace Sentinel.Domain.Notifications;

/// <summary>
/// How far along an expiry a subject was the last time its owner was told about it.
/// <para>
/// Ordered on purpose: the recurring notifier sends a message only when the current stage is
/// higher than the stored one, which is what stops the same warning arriving every sweep. The
/// values must keep their ordering — a new stage goes between existing numbers, not on the end.
/// </para>
/// </summary>
public enum ExpiryNoticeStage
{
    /// <summary>Nothing to say, and nothing has been said.</summary>
    None = 0,

    /// <summary>Approaching the end — still fully usable.</summary>
    Warning = 1,

    /// <summary>Past the paid window but still working, such as a grace period.</summary>
    Critical = 2,

    /// <summary>Over: expired, or out of quota.</summary>
    Ended = 3,
}
