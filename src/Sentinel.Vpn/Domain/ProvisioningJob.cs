using Sentinel.Domain.Common;

namespace Sentinel.Vpn.Domain;

public enum ProvisioningJobKind
{
    /// <summary>Create the client on a panel and attach it to the allowlisted inbounds.</summary>
    Provision = 0,

    /// <summary>Disable the client without deleting it, so it can be resumed.</summary>
    Suspend = 1,

    Resume = 2,

    /// <summary>Push changed terms — a renewal's new expiry, a topped-up allowance.</summary>
    UpdateTerms = 3,

    /// <summary>Zero the counters and re-enable across every attached inbound.</summary>
    ResetTraffic = 4,

    /// <summary>Remove the client from the panel for good.</summary>
    Decommission = 5,
}

public enum ProvisioningJobStatus
{
    Pending = 0,

    /// <summary>Claimed by a worker. Guarded by the concurrency token so two replicas cannot both run it.</summary>
    Running = 1,

    Succeeded = 2,

    /// <summary>
    /// Failed for a reason the panel stated. Safe to retry, because the panel answered and said no —
    /// so nothing was half-applied.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// The panel did not answer usably. The operation may or may not have taken effect.
    /// <para>
    /// This is the state the whole job table exists for. It is never retried: retrying a create that
    /// may already have succeeded produces a second client, and retrying a delete that may have
    /// succeeded produces a confusing error. Reconciliation reads the panel first and then decides.
    /// </para>
    /// </summary>
    NeedsReconciliation = 4,

    /// <summary>Given up on after too many safe retries. An operator has to look.</summary>
    Abandoned = 5,
}

/// <summary>
/// One unit of work against a panel.
/// <para>
/// Provisioning is not done inline on a member's request. It involves a third-party system that can
/// be slow, refuse, or answer nothing at all, and none of those should be a customer watching a
/// spinner. Recording the intent first and acting on it separately also means the intent survives a
/// process restart mid-operation — which is precisely when the outcome is unknown.
/// </para>
/// </summary>
public class ProvisioningJob : IConcurrencyAware, ITimestamped
{
    public const int ErrorMaxLength = 500;

    /// <summary>
    /// Safe retries before giving up. Only ever applied to <see cref="ProvisioningJobStatus.Failed"/>
    /// — the outcomes that are certain. An unknown outcome is not counted here because it is not
    /// retried at all.
    /// </summary>
    public const int MaxAttempts = 5;

    public Guid Id { get; set; }

    public Guid ServiceId { get; set; }

    public CustomerService? Service { get; set; }

    public ProvisioningJobKind Kind { get; set; }

    public ProvisioningJobStatus Status { get; set; } = ProvisioningJobStatus.Pending;

    public int Attempts { get; set; }

    /// <summary>
    /// When this job may next be picked up. Set to a backoff on a safe failure, and left in the past
    /// for a fresh job so the next sweep takes it.
    /// </summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Short and already redacted. Panel messages are truncated before they reach here.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// The server this job targets, captured when the job is created.
    /// <para>
    /// Not read from the service at run time: a migration changes the service's server, and a
    /// decommission job queued beforehand must still act on the panel it was written for — otherwise
    /// it would delete the client from its new home.
    /// </para>
    /// </summary>
    public Guid? TargetServerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }

    // ---- derived -----------------------------------------------------------------------

    /// <summary>Whether a worker may pick this up. Never true for an unknown outcome.</summary>
    public bool IsRunnableAt(DateTimeOffset instant) =>
        Status == ProvisioningJobStatus.Pending
        || (Status == ProvisioningJobStatus.Failed
            && Attempts < MaxAttempts
            && NextAttemptAt <= instant);

    public bool IsFinished =>
        Status is ProvisioningJobStatus.Succeeded
            or ProvisioningJobStatus.Abandoned
            or ProvisioningJobStatus.NeedsReconciliation;
}
