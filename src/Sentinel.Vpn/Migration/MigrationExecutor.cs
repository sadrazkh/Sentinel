using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentinel.Application.Auditing;
using Sentinel.Domain.Auditing;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Panel;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Provisioning;
using Sentinel.Vpn.Servers;

namespace Sentinel.Vpn.Migration;

/// <summary>
/// Carries out a planned migration, one panel call per sweep.
/// <para>
/// The order is the whole design: <b>create at the destination → read it back → only then remove the
/// source</b>. Every other ordering has a window in which the customer has no working client, and the
/// window is unbounded, because the step that would give them one back is the step that just failed.
/// </para>
/// <para>
/// The read-back is not ceremony. A create that answered "success" tells us the panel accepted the
/// request; reading the client back tells us it is there. The difference matters exactly once — at
/// the moment the source is about to be deleted — and that is the only irreversible step here.
/// </para>
/// <para>
/// Advancing one step per claim keeps the unknown-outcome rule tractable: at most one call is ever in
/// doubt, and the step the migration is parked at says which one it was.
/// </para>
/// </summary>
public sealed class MigrationExecutor : IMigrationExecutor
{
    private readonly IVpnDbContext _db;
    private readonly IThreeXUiClient _panel;
    private readonly IVpnServerAdminService _servers;
    private readonly ICapacityService _capacity;
    private readonly IAuditService _audit;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MigrationExecutor> _logger;

    public MigrationExecutor(
        IVpnDbContext db,
        IThreeXUiClient panel,
        IVpnServerAdminService servers,
        ICapacityService capacity,
        IAuditService audit,
        TimeProvider timeProvider,
        ILogger<MigrationExecutor> logger)
    {
        _db = db;
        _panel = panel;
        _servers = servers;
        _capacity = capacity;
        _audit = audit;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> RunPendingAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var candidates = await _db.ServiceMigrations
            .Where(migration =>
                (migration.Step == MigrationStep.Planned
                 || migration.Step == MigrationStep.Creating
                 || migration.Step == MigrationStep.Verifying
                 || migration.Step == MigrationStep.Detaching)
                && migration.Attempts < ServiceMigration.MaxAttempts
                && (migration.NextAttemptAt == null || migration.NextAttemptAt <= now))
            .OrderBy(migration => migration.NextAttemptAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var advanced = 0;

        foreach (var migration in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await TryClaimAsync(migration, cancellationToken))
            {
                continue;
            }

            await AdvanceAsync(migration, cancellationToken);
            advanced++;
        }

        return advanced;
    }

    /// <summary>
    /// Marks the attempt, which is also how two replicas avoid both calling the panel: the write is
    /// guarded by the migration's concurrency token, so exactly one wins.
    /// </summary>
    private async Task<bool> TryClaimAsync(
        ServiceMigration migration,
        CancellationToken cancellationToken)
    {
        migration.Attempts++;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await _db.ReloadAsync(migration, cancellationToken);
            return false;
        }
    }

    private async Task AdvanceAsync(
        ServiceMigration migration,
        CancellationToken cancellationToken)
    {
        var service = await _db.CustomerServices
            .FirstOrDefaultAsync(candidate => candidate.Id == migration.ServiceId, cancellationToken);

        if (service is null || service.PanelClientEmail is null)
        {
            await AbandonAsync(migration, "The service no longer exists.", cancellationToken);
            return;
        }

        switch (migration.Step)
        {
            case MigrationStep.Planned:
            case MigrationStep.Creating:
                await CreateAtDestinationAsync(migration, service, cancellationToken);
                return;

            case MigrationStep.Verifying:
                await VerifyDestinationAsync(migration, service, cancellationToken);
                return;

            case MigrationStep.Detaching:
                await DetachSourceAsync(migration, service, cancellationToken);
                return;

            default:
                return;
        }
    }

    // -------------------------------------------------------------------------- the steps ----

    /// <summary>
    /// Creates the client on the destination panel with the terms frozen at planning time.
    /// <para>
    /// Idempotent by looking first: a create whose answer was lost may have landed, and this step can
    /// be reached again after reconciliation. Finding the client already there advances rather than
    /// creating a second one.
    /// </para>
    /// </summary>
    private async Task CreateAtDestinationAsync(
        ServiceMigration migration,
        CustomerService service,
        CancellationToken cancellationToken)
    {
        var endpoint = await _servers.ResolveEndpointAsync(
            migration.DestinationServerId, cancellationToken);

        if (endpoint is null)
        {
            await RetryAsync(migration, "The destination server's token could not be read.", cancellationToken);
            return;
        }

        // Recorded before the call, so a process that dies mid-create leaves a migration that says
        // "a create may be outstanding" rather than one that still says "nothing has happened".
        migration.Step = MigrationStep.Creating;
        await _db.SaveChangesAsync(cancellationToken);

        var existing = await _panel.GetClientAsync(endpoint, service.PanelClientEmail!, cancellationToken);

        if (existing.Outcome == PanelOutcome.UnknownOutcome)
        {
            await ParkAsync(migration, service, existing.Message, cancellationToken);
            return;
        }

        if (existing.IsSuccess)
        {
            // Already there — a previous attempt landed after all. Straight to verification, which
            // will check its terms rather than assuming them.
            await MoveToAsync(migration, MigrationStep.Verifying, cancellationToken);
            return;
        }

        var inbounds = await _db.ServiceInboundBindings
            .Where(binding => binding.ServiceId == service.Id
                              && binding.ServerId == migration.DestinationServerId)
            .ToListAsync(cancellationToken);

        if (inbounds.Count == 0)
        {
            await AbandonAsync(
                migration, "No inbound bindings were recorded for the destination.", cancellationToken);
            return;
        }

        var created = await _panel.CreateClientAsync(
            endpoint,
            BuildRequest(migration, service, inbounds.Select(binding => binding.InboundId).ToList()),
            cancellationToken);

        if (created.Outcome == PanelOutcome.UnknownOutcome)
        {
            await ParkAsync(migration, service, created.Message, cancellationToken);
            return;
        }

        if (!created.IsSuccess)
        {
            await RetryAsync(migration, created.Message, cancellationToken);
            return;
        }

        await MoveToAsync(migration, MigrationStep.Verifying, cancellationToken);
    }

    /// <summary>
    /// Reads the destination client back and checks it is what was asked for.
    /// <para>
    /// This is the gate in front of the only irreversible step. A create that returned success but
    /// produced a client with the wrong allowance or the wrong inbounds would, without this, be
    /// followed immediately by deleting the customer's working one.
    /// </para>
    /// </summary>
    private async Task VerifyDestinationAsync(
        ServiceMigration migration,
        CustomerService service,
        CancellationToken cancellationToken)
    {
        var endpoint = await _servers.ResolveEndpointAsync(
            migration.DestinationServerId, cancellationToken);

        if (endpoint is null)
        {
            await RetryAsync(migration, "The destination server's token could not be read.", cancellationToken);
            return;
        }

        var client = await _panel.GetClientAsync(endpoint, service.PanelClientEmail!, cancellationToken);

        if (client.Outcome == PanelOutcome.UnknownOutcome)
        {
            await ParkAsync(migration, service, client.Message, cancellationToken);
            return;
        }

        if (!client.IsSuccess)
        {
            // The create said yes and the client is not there. Certain, so safe to go round again —
            // and the source has not been touched, so the customer is still being served.
            await RetryAsync(
                migration, "The destination client was not found after creation.", cancellationToken);
            return;
        }

        var expectedInbounds = await _db.ServiceInboundBindings
            .Where(binding => binding.ServiceId == service.Id
                              && binding.ServerId == migration.DestinationServerId)
            .ToListAsync(cancellationToken);

        var actual = client.Value!;

        // Checked against what was asked for, not against the service row: the service still carries
        // the source's terms until the migration completes.
        var termsMatch = actual.TotalAllowanceBytes == migration.RemainingBytes
                         && NearlyEqual(actual.ExpiresAt, migration.ExpiresAt);

        var attached = expectedInbounds
            .Select(binding => binding.InboundId)
            .All(actual.InboundIds.Contains);

        if (!termsMatch || !attached)
        {
            _logger.LogWarning(
                "Migration {MigrationId}: the destination client does not match what was asked for "
                + "(terms {TermsMatch}, inbounds {Attached}). Pushing the terms again.",
                migration.Id,
                termsMatch,
                attached);

            var corrected = await _panel.UpdateClientAsync(
                endpoint,
                BuildRequest(
                    migration, service, expectedInbounds.Select(binding => binding.InboundId).ToList()),
                cancellationToken);

            if (corrected.Outcome == PanelOutcome.UnknownOutcome)
            {
                await ParkAsync(migration, service, corrected.Message, cancellationToken);
                return;
            }

            // Left at Verifying either way: the next sweep reads it back again rather than trusting
            // the correction it just made.
            await RetryAsync(
                migration,
                corrected.IsSuccess
                    ? "The destination client was corrected; re-checking."
                    : corrected.Message,
                cancellationToken);

            return;
        }

        var now = _timeProvider.GetUtcNow();

        foreach (var binding in expectedInbounds)
        {
            binding.State = BindingState.Attached;
            binding.LastVerifiedAt = now;
        }

        // From here the customer is live on two panels. Stamped so an operator can see how long the
        // window has been open — both panels count traffic against their own copy of the allowance.
        migration.DualActiveSince = now;
        migration.Step = MigrationStep.Detaching;
        migration.Attempts = 0;
        migration.NextAttemptAt = now;
        migration.LastError = null;

        await _audit.RecordAsync(
            AuditEntry.For(
                VpnAuditActions.ServiceMigrationVerified, nameof(ServiceMigration), migration.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("serviceId", service.Id)
                    .Set("destinationServerId", migration.DestinationServerId)
                    .Set("allowanceBytes", migration.RemainingBytes),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Removes the client from the source panel and moves the service across.
    /// <para>
    /// <c>keepTraffic: true</c>, unlike a decommission: the source's counters are the record of what
    /// this customer used before the move, and a support question about their quota a week later is
    /// answered from it.
    /// </para>
    /// </summary>
    private async Task DetachSourceAsync(
        ServiceMigration migration,
        CustomerService service,
        CancellationToken cancellationToken)
    {
        var endpoint = await _servers.ResolveEndpointAsync(migration.SourceServerId, cancellationToken);

        if (endpoint is null)
        {
            await RetryAsync(migration, "The source server's token could not be read.", cancellationToken);
            return;
        }

        var deleted = await _panel.DeleteClientAsync(
            endpoint, service.PanelClientEmail!, keepTraffic: true, cancellationToken);

        if (deleted.Outcome == PanelOutcome.UnknownOutcome)
        {
            await ParkAsync(migration, service, deleted.Message, cancellationToken);
            return;
        }

        // NotFound is success here: the client is gone, which is the whole objective, and how it came
        // to be gone does not change what has to happen next.
        if (!deleted.IsSuccess && deleted.Outcome != PanelOutcome.NotFound)
        {
            await RetryAsync(migration, deleted.Message, cancellationToken);
            return;
        }

        await CompleteAsync(migration, service, cancellationToken);
    }

    /// <summary>
    /// Moves the service onto the destination and closes the migration.
    /// <para>
    /// The allowance is rebased: the destination client starts at zero used with the remaining
    /// allowance, so the service row has to say the same thing or the member's page would show them
    /// a quota that has already been spent.
    /// </para>
    /// </summary>
    private async Task CompleteAsync(
        ServiceMigration migration,
        CustomerService service,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var sourceServerId = migration.SourceServerId;

        foreach (var binding in await _db.ServiceInboundBindings
                     .Where(binding => binding.ServiceId == service.Id
                                       && binding.ServerId == sourceServerId)
                     .ToListAsync(cancellationToken))
        {
            binding.State = BindingState.Detached;
            binding.LastVerifiedAt = now;
        }

        service.ServerId = migration.DestinationServerId;
        service.TrafficBytes = migration.RemainingBytes;
        service.UsedBytes = 0;
        service.LastUsageSyncAt = now;
        service.LastError = null;

        // The expiry is not touched. It was copied into the migration at planning time precisely so
        // nothing along this path could recompute it.

        migration.Step = MigrationStep.Completed;
        migration.CompletedAt = now;
        migration.LastError = null;

        // Released only now, when the source client is confirmed gone. Releasing at any earlier step
        // would let another order take a slot that was still occupied.
        await _capacity.ReleaseAsync(sourceServerId, cancellationToken);

        await _audit.RecordAsync(
            AuditEntry.For(
                VpnAuditActions.ServiceMigrationCompleted, nameof(ServiceMigration), migration.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("serviceId", service.Id)
                    .Set("userId", service.UserId)
                    .Set("sourceServerId", sourceServerId)
                    .Set("destinationServerId", migration.DestinationServerId)
                    .Set("allowanceBytes", migration.RemainingBytes)
                    .Set("dualActiveSeconds",
                        migration.DualActiveSince is { } since ? (int)(now - since).TotalSeconds : 0),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Migration {MigrationId} completed: service {ServiceId} moved from {Source} to {Destination}.",
            migration.Id,
            service.Id,
            sourceServerId,
            migration.DestinationServerId);
    }

    // ---------------------------------------------------------------------- reconciliation ----

    public async Task<int> ReconcileAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var parked = await _db.ServiceMigrations
            .Where(migration => migration.Step == MigrationStep.NeedsAttention)
            .OrderBy(migration => migration.UpdatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var resolved = 0;

        foreach (var migration in parked)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ReconcileOneAsync(migration, cancellationToken))
            {
                resolved++;
            }
        }

        return resolved;
    }

    /// <summary>
    /// Works out which of the two possible worlds is real, by reading both panels.
    /// <para>
    /// The pair of answers — is the client at the source, is it at the destination — determines the
    /// step to resume from without anything having to be guessed. Notably, "at both" is not an error:
    /// it is the ordinary mid-migration state, and the answer is simply to carry on detaching.
    /// </para>
    /// </summary>
    private async Task<bool> ReconcileOneAsync(
        ServiceMigration migration,
        CancellationToken cancellationToken)
    {
        var service = await _db.CustomerServices
            .FirstOrDefaultAsync(candidate => candidate.Id == migration.ServiceId, cancellationToken);

        if (service?.PanelClientEmail is null)
        {
            await AbandonAsync(migration, "The service no longer exists.", cancellationToken);
            return true;
        }

        var source = await LookUpAsync(migration.SourceServerId, service.PanelClientEmail, cancellationToken);
        var destination = await LookUpAsync(
            migration.DestinationServerId, service.PanelClientEmail, cancellationToken);

        // Either panel still silent: nothing may be decided. Both readings are needed, because the
        // decision depends on the pair and not on either alone.
        if (source is null || destination is null)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        string resolution;

        if (destination.Value)
        {
            if (source.Value)
            {
                // Both live. The ordinary mid-migration state — resume at the step that removes the
                // source. Verification is redone first, because this path may have been reached from
                // a create whose result was never confirmed.
                migration.Step = MigrationStep.Verifying;
                resolution = "resumeVerify";
            }
            else
            {
                // Destination only: the detach landed and the answer was lost. Finish the bookkeeping
                // the step never got to.
                await CompleteAsync(migration, service, cancellationToken);
                return true;
            }
        }
        else if (source.Value)
        {
            // Source only: nothing was created at the destination, or it was created and removed.
            // Start the destination again — the customer is still being served throughout.
            migration.Step = MigrationStep.Creating;
            resolution = "resumeCreate";
        }
        else
        {
            // Neither. The client has vanished from both panels, which no step of this saga produces —
            // something outside the portal removed it. Not something to guess at.
            migration.Step = MigrationStep.Abandoned;
            migration.CompletedAt = now;
            migration.LastError = "The client is on neither panel.";

            service.Status = CustomerServiceStatus.NeedsAttention;
            service.LastError = "The client is on neither the source nor the destination panel.";

            resolution = "missingEverywhere";
        }

        if (migration.Step != MigrationStep.Abandoned)
        {
            migration.Attempts = 0;
            migration.NextAttemptAt = now;
            migration.LastError = null;
        }

        await _audit.RecordAsync(
            AuditEntry.For(
                VpnAuditActions.ServiceMigrationReconciled, nameof(ServiceMigration), migration.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("serviceId", migration.ServiceId)
                    .Set("onSource", source.Value)
                    .Set("onDestination", destination.Value)
                    .Set("resolution", resolution),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Migration {MigrationId} reconciled: source {OnSource}, destination {OnDestination} → {Resolution}.",
            migration.Id,
            source.Value,
            destination.Value,
            resolution);

        return true;
    }

    /// <summary>
    /// Whether the client is on a panel. <c>null</c> means the panel could not be asked — which is
    /// different from "no", and must never be read as one.
    /// </summary>
    private async Task<bool?> LookUpAsync(
        Guid serverId,
        string email,
        CancellationToken cancellationToken)
    {
        var endpoint = await _servers.ResolveEndpointAsync(serverId, cancellationToken);

        if (endpoint is null)
        {
            return null;
        }

        var client = await _panel.GetClientAsync(endpoint, email, cancellationToken);

        return client.Outcome switch
        {
            PanelOutcome.Success => true,
            PanelOutcome.NotFound => false,
            _ => null,
        };
    }

    // --------------------------------------------------------------------------- outcomes ----

    private async Task MoveToAsync(
        ServiceMigration migration,
        MigrationStep step,
        CancellationToken cancellationToken)
    {
        migration.Step = step;

        // The attempt counter is per step, not per migration: a step that succeeded should not leave
        // the next one with fewer tries than it is entitled to.
        migration.Attempts = 0;
        migration.NextAttemptAt = _timeProvider.GetUtcNow();
        migration.LastError = null;

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Parks a step whose outcome nobody knows.
    /// <para>
    /// The same rule as provisioning, and it bites harder here: a repeated create makes a second
    /// client, and a repeated delete could remove the customer's only working one. The step is left
    /// recorded so reconciliation knows which call was in doubt.
    /// </para>
    /// </summary>
    private async Task ParkAsync(
        ServiceMigration migration,
        CustomerService service,
        string? message,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Migration {MigrationId} step {Step} ended without a usable answer. "
            + "Parked; it will not be retried until both panels have been read.",
            migration.Id,
            migration.Step);

        migration.LastError = Truncate(message ?? "The panel did not answer.");

        await _audit.RecordAsync(
            AuditEntry.For(
                VpnAuditActions.ServiceMigrationNeedsAttention,
                nameof(ServiceMigration),
                migration.Id) with
            {
                Result = AuditResult.Failure,
                Metadata = AuditMetadata.Create()
                    .Set("serviceId", service.Id)
                    .Set("step", migration.Step)
                    .Set("reason", "unknownOutcome"),
            },
            cancellationToken);

        migration.Step = MigrationStep.NeedsAttention;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RetryAsync(
        ServiceMigration migration,
        string? message,
        CancellationToken cancellationToken)
    {
        migration.LastError = Truncate(message ?? "The panel refused the request.");

        if (migration.Attempts >= ServiceMigration.MaxAttempts)
        {
            await AbandonAsync(migration, migration.LastError, cancellationToken);
            return;
        }

        migration.NextAttemptAt = _timeProvider.GetUtcNow()
            .Add(TimeSpan.FromSeconds(Math.Pow(2, migration.Attempts) * 15));

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Gives up.
    /// <para>
    /// The destination's capacity is released only when the destination was never verified. Past that
    /// point the client is really there, and handing the slot back would let another service be
    /// placed on top of it.
    /// </para>
    /// </summary>
    private async Task AbandonAsync(
        ServiceMigration migration,
        string? message,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        if (migration.DualActiveSince is null)
        {
            await _capacity.ReleaseAsync(migration.DestinationServerId, cancellationToken);
        }

        migration.Step = MigrationStep.Abandoned;
        migration.CompletedAt = now;
        migration.LastError = Truncate(message);

        _logger.LogError(
            "Migration {MigrationId} abandoned after {Attempts} attempts: {Reason}",
            migration.Id,
            migration.Attempts,
            migration.LastError);

        await _audit.RecordAsync(
            AuditEntry.For(
                VpnAuditActions.ServiceMigrationAbandoned, nameof(ServiceMigration), migration.Id) with
            {
                Result = AuditResult.Failure,
                Metadata = AuditMetadata.Create()
                    .Set("serviceId", migration.ServiceId)
                    .Set("destinationServerId", migration.DestinationServerId),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }

    // --------------------------------------------------------------------------- helpers ----

    /// <summary>
    /// The destination client's payload, built from the <em>migration</em> rather than the service.
    /// <para>
    /// That is the point of freezing the terms at planning time: the service row still describes the
    /// source until the move completes, and reading it here would give the destination the full
    /// original allowance instead of what is left.
    /// </para>
    /// </summary>
    private static PanelClientRequest BuildRequest(
        ServiceMigration migration,
        CustomerService service,
        IReadOnlyList<int> inboundIds) =>
        new(
            service.PanelClientEmail!,
            inboundIds,
            migration.RemainingBytes,
            migration.ExpiresAt,
            service.DeviceLimit,
            Enabled: service.Status != CustomerServiceStatus.Suspended);

    /// <summary>
    /// Compares two expiry instants the way the panel stores them — epoch milliseconds, so a
    /// sub-second difference is a round-trip artefact and not a mismatch.
    /// </summary>
    private static bool NearlyEqual(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return Math.Abs((left.Value - right.Value).TotalSeconds) < 1;
    }

    private static string? Truncate(string? value) =>
        value is null
            ? null
            : value.Length <= ServiceMigration.ErrorMaxLength
                ? value
                : value[..ServiceMigration.ErrorMaxLength];
}
