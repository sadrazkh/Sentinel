using Sentinel.Domain.Common;

namespace Sentinel.Vpn.Domain;

/// <summary>
/// Where a migration has got to.
/// <para>
/// The order matters and is deliberate: the client is created at the destination and
/// <b>independently verified</b> before anything at the source is touched. Delete-then-recreate would
/// be one call shorter and would leave the customer with no working configuration for however long
/// the second call took — or for ever, if it failed.
/// </para>
/// </summary>
public enum MigrationStep
{
    /// <summary>Recorded, with the terms already computed. Nothing has been sent to a panel.</summary>
    Planned = 0,

    /// <summary>The client is being created on the destination panel.</summary>
    Creating = 1,

    /// <summary>
    /// Created, but not yet believed. The next step reads it back from the destination — a create
    /// that answered "success" is evidence, not proof, and this is the one place where the difference
    /// decides whether a working client gets deleted.
    /// </summary>
    Verifying = 2,

    /// <summary>
    /// Verified at the destination and still present at the source. The customer is served by two
    /// panels at once — see <see cref="ServiceMigration.IsDualActive"/>.
    /// </summary>
    Detaching = 3,

    Completed = 4,

    /// <summary>
    /// A panel step ended without a usable answer. Never advanced automatically from here: the
    /// reconciler reads both panels before anything else moves.
    /// </summary>
    NeedsAttention = 5,

    /// <summary>Given up on. Whatever state the panels are in is what an operator will find.</summary>
    Abandoned = 6,

    /// <summary>
    /// Called off before the destination was verified, and the destination client removed again.
    /// Only reachable while the source is still untouched, so the customer never lost service.
    /// </summary>
    RolledBack = 7,
}

/// <summary>
/// Moving one customer's service from one panel to another.
/// <para>
/// A durable record rather than a sequence of jobs, because the interesting state is <em>between</em>
/// the calls. "Created at the destination but not yet removed from the source" is a real condition a
/// customer can be in for minutes, it is visible to an operator, and it is the state that has to
/// survive a process restart intact — a job history would leave it to be reconstructed.
/// </para>
/// <para>
/// Every term the destination client gets is computed here, once, at planning time. Nothing about a
/// migration is supplied by the caller beyond "this service, that server".
/// </para>
/// </summary>
public class ServiceMigration : IConcurrencyAware, ITimestamped
{
    public const int ErrorMaxLength = 500;

    /// <summary>
    /// Safe retries per step. Lower than provisioning's five on purpose: a migration holds capacity on
    /// two servers while it runs, and a step that has refused three times is not going to stop.
    /// </summary>
    public const int MaxAttempts = 3;

    public Guid Id { get; set; }

    public Guid ServiceId { get; set; }

    public CustomerService? Service { get; set; }

    public Guid SourceServerId { get; set; }

    public Guid DestinationServerId { get; set; }

    public MigrationStep Step { get; set; } = MigrationStep.Planned;

    // ---- terms, computed server-side at planning time -----------------------------------

    /// <summary>
    /// The allowance the destination client is given: what is <em>left</em>, read from the source
    /// panel at planning time.
    /// <para>
    /// Computed here and stored, never accepted from a caller and never recomputed later. A request
    /// shape carrying this number would be a customer writing their own quota; recomputing it at
    /// execution time would let usage recorded during the migration silently shrink it.
    /// </para>
    /// <para>
    /// Zero means unlimited, matching the panel's convention — so an unlimited service stays
    /// unlimited rather than being migrated to a quota of nothing.
    /// </para>
    /// </summary>
    public long RemainingBytes { get; set; }

    /// <summary>
    /// The service's expiry, copied unchanged.
    /// <para>
    /// Preserved rather than recalculated from the plan's duration: a migration is not a renewal, and
    /// re-deriving it would quietly hand the customer a fresh month — or take one away from somebody
    /// halfway through theirs.
    /// </para>
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Usage at the source when the migration was planned, for the record and the audit.</summary>
    public long SourceUsedBytes { get; set; }

    // ---- progress -----------------------------------------------------------------------

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>
    /// When the destination was confirmed live while the source still was. The start of the window
    /// in which the customer's quota is being counted by two panels.
    /// </summary>
    public DateTimeOffset? DualActiveSince { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Short and already redacted. Panel messages are truncated before they reach here.</summary>
    public string? LastError { get; set; }

    /// <summary>Why an operator asked for this. Back-office only; never shown to the member.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }

    // ---- derived -----------------------------------------------------------------------

    public bool IsFinished =>
        Step is MigrationStep.Completed or MigrationStep.Abandoned or MigrationStep.RolledBack;

    /// <summary>
    /// Whether the customer currently has a live client on both panels.
    /// <para>
    /// Not a fault — it is the deliberate cost of verifying the destination before touching the
    /// source. It is exposed because it has a real consequence: both panels count traffic against
    /// their own copy of the allowance, so a long window lets usage be spent twice. Anything left
    /// here for more than a few minutes wants an operator.
    /// </para>
    /// </summary>
    public bool IsDualActive => Step is MigrationStep.Detaching or MigrationStep.NeedsAttention
                                && DualActiveSince is not null;

    public TimeSpan? DualActiveFor(DateTimeOffset instant) =>
        DualActiveSince is { } since && !IsFinished ? instant - since : null;

    /// <summary>Whether a worker may pick this up. Never true for a step that is parked or done.</summary>
    public bool IsRunnableAt(DateTimeOffset instant) =>
        Step is MigrationStep.Planned or MigrationStep.Creating
            or MigrationStep.Verifying or MigrationStep.Detaching
        && Attempts < MaxAttempts
        && (NextAttemptAt is not { } next || next <= instant);
}
