using Sentinel.Application.Common;
using Sentinel.Vpn.Domain;

namespace Sentinel.Vpn.Migration;

/// <summary>
/// What an operator supplies to move a service.
/// <para>
/// A service, and either a destination server or a destination country. Nothing else — and in
/// particular no allowance, no expiry and no inbound. Those are computed from the source panel and
/// the service row when the migration is planned; a bindable property for any of them would be the
/// customer's terms taken from a form post.
/// </para>
/// </summary>
public sealed record MigrateServiceRequest(
    Guid ServiceId,

    /// <summary>
    /// The destination. Null lets <see cref="Provisioning.ServerSelector"/> choose within
    /// <paramref name="CountryCode"/>, which is the usual case: an operator moves a customer off a
    /// failing server and does not care which healthy one they land on.
    /// </summary>
    Guid? DestinationServerId,

    string? CountryCode,
    string? Reason);

/// <summary>One migration as an operator sees it.</summary>
public sealed record MigrationView(
    Guid Id,
    Guid ServiceId,
    string UserName,
    string PlanNameEn,
    Guid SourceServerId,
    string? SourceServerKey,
    Guid DestinationServerId,
    string? DestinationServerKey,
    MigrationStep Step,
    long RemainingBytes,
    DateTimeOffset? ExpiresAt,
    int Attempts,
    DateTimeOffset? DualActiveSince,
    DateTimeOffset? CompletedAt,
    string? LastError,
    DateTimeOffset CreatedAt)
{
    public bool IsFinished =>
        Step is MigrationStep.Completed or MigrationStep.Abandoned or MigrationStep.RolledBack;

    /// <summary>
    /// The customer is live on two panels right now. Surfaced separately from the step because it is
    /// the one condition here that costs something — both panels count the same allowance.
    /// </summary>
    public bool IsDualActive =>
        DualActiveSince is not null && Step is MigrationStep.Detaching or MigrationStep.NeedsAttention;

    public TimeSpan? DualActiveFor(DateTimeOffset instant) =>
        DualActiveSince is { } since && !IsFinished ? instant - since : null;
}

public static class MigrationErrors
{
    public const string ServiceNotFound = "admin.error.migrationServiceNotFound";
    public const string AlreadyInFlight = "admin.error.migrationInFlight";
    public const string NotMigratable = "admin.error.migrationNotMigratable";
    public const string SameServer = "admin.error.migrationSameServer";
    public const string DestinationNotFound = "admin.error.migrationDestinationNotFound";
    public const string DestinationUnusable = "admin.error.migrationDestinationUnusable";
    public const string NoCapacity = "admin.error.migrationNoCapacity";
    public const string SourceUnreadable = "admin.error.migrationSourceUnreadable";
    public const string NotRollbackable = "admin.error.migrationNotRollbackable";
}

/// <summary>
/// Plans and oversees moving a service between panels.
/// <para>
/// Planning and execution are separate for the same reason they are in provisioning: the record has
/// to exist before the first panel call, or a process that dies mid-call leaves nothing behind
/// saying what was being attempted.
/// </para>
/// </summary>
public interface IServiceMigrationManager
{
    /// <summary>
    /// Records a migration and computes its terms. Reads the source panel once, to establish how much
    /// allowance is actually left — the cached counter can be a sweep out of date, and a migration
    /// that hands over the wrong number is one the customer notices.
    /// </summary>
    Task<OperationResult<Guid>> PlanAsync(
        MigrateServiceRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls off a migration that has not yet been verified at the destination, removing whatever was
    /// created there. Refused once the source has been touched — at that point the only way out is
    /// forward, and pretending otherwise would risk deleting the customer's only working client.
    /// </summary>
    Task<OperationResult> RollBackAsync(Guid migrationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MigrationView>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The unfinished migration for a service, if there is one.</summary>
    Task<MigrationView?> ActiveForServiceAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Advances migrations one step per sweep.
/// <para>
/// One panel call per claim, deliberately. It is what keeps an unknown outcome tractable: exactly one
/// call is ever in doubt, and the step the migration is parked at says which one.
/// </para>
/// </summary>
public interface IMigrationExecutor
{
    Task<int> RunPendingAsync(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves migrations parked at <see cref="MigrationStep.NeedsAttention"/> by reading both
    /// panels and working out which of the two possible worlds is the real one.
    /// </summary>
    Task<int> ReconcileAsync(int batchSize, CancellationToken cancellationToken = default);
}
