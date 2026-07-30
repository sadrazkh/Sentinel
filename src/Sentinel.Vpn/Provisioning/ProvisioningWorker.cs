using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentinel.Application.Auditing;
using Sentinel.Domain.Auditing;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Panel;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Servers;

namespace Sentinel.Vpn.Provisioning;

/// <summary>
/// Runs one provisioning job against a panel.
/// <para>
/// The rule that shapes this whole class: a panel write whose outcome is unknown is
/// <b>never retried</b>. Retrying a create that may already have succeeded produces a second client
/// on the panel — a customer with two configurations and a quota counted twice. Retrying a delete
/// that may have succeeded produces a confusing failure and an operator chasing a client that is
/// already gone.
/// </para>
/// <para>
/// So an unknown outcome parks the job in
/// <see cref="ProvisioningJobStatus.NeedsReconciliation"/> and the service in
/// <see cref="CustomerServiceStatus.NeedsAttention"/>. The reconciliation sweep then <em>reads</em>
/// the panel to establish what is actually true, and only then decides what to do. Only outcomes the
/// panel stated — a refusal, a 404 — are retried, because those are certain that nothing was applied.
/// </para>
/// </summary>
public interface IProvisioningExecutor
{
    /// <summary>
    /// Claims and runs at most <paramref name="batchSize"/> runnable jobs. Returns how many ran.
    /// </summary>
    Task<int> RunPendingAsync(int batchSize, CancellationToken cancellationToken = default);
}

public sealed class ProvisioningExecutor : IProvisioningExecutor
{
    private readonly IVpnDbContext _db;
    private readonly IThreeXUiClient _panel;
    private readonly IVpnServerAdminService _servers;
    private readonly ICapacityService _capacity;
    private readonly IAuditService _audit;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProvisioningExecutor> _logger;

    public ProvisioningExecutor(
        IVpnDbContext db,
        IThreeXUiClient panel,
        IVpnServerAdminService servers,
        ICapacityService capacity,
        IAuditService audit,
        TimeProvider timeProvider,
        ILogger<ProvisioningExecutor> logger)
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

        // NeedsReconciliation is deliberately absent from this filter. Those jobs are not work; they
        // are questions for the reconciliation sweep.
        var candidates = await _db.ProvisioningJobs
            .Where(job => job.Status == ProvisioningJobStatus.Pending
                          || (job.Status == ProvisioningJobStatus.Failed
                              && job.Attempts < ProvisioningJob.MaxAttempts
                              && job.NextAttemptAt <= now))
            .OrderBy(job => job.NextAttemptAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var ran = 0;

        foreach (var job in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await TryClaimAsync(job, now, cancellationToken))
            {
                // Another replica got it. Not an error — this is how the claim is meant to work.
                continue;
            }

            await ExecuteAsync(job, cancellationToken);
            ran++;
        }

        return ran;
    }

    /// <summary>
    /// Marks a job as running.
    /// <para>
    /// This is how two replicas avoid both calling the panel: claiming is a write guarded by the
    /// job's concurrency token, so exactly one wins. A distributed lock would be more machinery for
    /// the same guarantee the database already gives.
    /// </para>
    /// </summary>
    private async Task<bool> TryClaimAsync(
        ProvisioningJob job,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        job.Status = ProvisioningJobStatus.Running;
        job.StartedAt = now;
        job.Attempts++;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await _db.ReloadAsync(job, cancellationToken);
            return false;
        }
    }

    private async Task ExecuteAsync(ProvisioningJob job, CancellationToken cancellationToken)
    {
        var service = await _db.CustomerServices
            .FirstOrDefaultAsync(candidate => candidate.Id == job.ServiceId, cancellationToken);

        if (service is null)
        {
            // The service was deleted outright. Nothing to do, and nothing to reconcile.
            await FinishAsync(job, ProvisioningJobStatus.Succeeded, null, cancellationToken);
            return;
        }

        // The job's own target, not the service's current server: a migration moves the service, and
        // a job queued before that must still act on the panel it was written for.
        var serverId = job.TargetServerId ?? service.ServerId;

        if (serverId is null || service.PanelClientEmail is null)
        {
            await FailPermanentlyAsync(
                job, service, "The service has no server or panel identifier.", cancellationToken);
            return;
        }

        var endpoint = await _servers.ResolveEndpointAsync(serverId.Value, cancellationToken);

        if (endpoint is null)
        {
            // A credential that cannot be decrypted is a certain failure — nothing was sent — so it
            // is safe to retry after an operator re-enters the token.
            await FailRetryablyAsync(
                job, service, "The server's stored token could not be read.", cancellationToken);
            return;
        }

        var outcome = job.Kind switch
        {
            ProvisioningJobKind.Provision => await ProvisionAsync(endpoint, service, cancellationToken),
            ProvisioningJobKind.Suspend => await SetEnabledAsync(endpoint, service, false, cancellationToken),
            ProvisioningJobKind.Resume => await SetEnabledAsync(endpoint, service, true, cancellationToken),
            ProvisioningJobKind.UpdateTerms => await UpdateTermsAsync(endpoint, service, cancellationToken),
            ProvisioningJobKind.ResetTraffic => await ResetTrafficAsync(endpoint, service, cancellationToken),
            ProvisioningJobKind.Decommission => await DecommissionAsync(endpoint, service, serverId.Value, cancellationToken),
            _ => PanelResult<bool>.Failure(PanelOutcome.Rejected, "Unknown job kind."),
        };

        if (outcome.IsSuccess)
        {
            await SucceedAsync(job, service, cancellationToken);
            return;
        }

        // The one branch this class exists for.
        if (outcome.Outcome == PanelOutcome.UnknownOutcome)
        {
            await ParkForReconciliationAsync(job, service, outcome.Message, cancellationToken);
            return;
        }

        // Everything else is certain: the panel answered, or the address was refused before anything
        // was sent. Safe to retry.
        await FailRetryablyAsync(job, service, outcome.Message, cancellationToken);
    }

    // ------------------------------------------------------------------------ operations ----

    private async Task<PanelResult<bool>> ProvisionAsync(
        PanelEndpoint endpoint,
        CustomerService service,
        CancellationToken cancellationToken)
    {
        var inbounds = await _db.ServiceInboundBindings
            .Where(binding => binding.ServiceId == service.Id
                              && binding.ServerId == service.ServerId)
            .ToListAsync(cancellationToken);

        if (inbounds.Count == 0)
        {
            return PanelResult<bool>.Failure(
                PanelOutcome.Rejected, "No inbound bindings were recorded for this service.");
        }

        // Idempotence, and the reason a repeated Provision is harmless: if the client is already
        // there — because a previous attempt's outcome was unknown and reconciliation queued this
        // one — the terms are pushed instead of a second client being created.
        var existing = await _panel.GetClientAsync(endpoint, service.PanelClientEmail!, cancellationToken);

        if (existing.IsSuccess)
        {
            _logger.LogInformation(
                "Service {ServiceId} already exists on the panel; updating its terms instead.",
                service.Id);

            return await UpdateTermsAsync(endpoint, service, cancellationToken);
        }

        if (existing.Outcome == PanelOutcome.UnknownOutcome)
        {
            // Could not even establish whether it exists. Nothing may be written.
            return PanelResult<bool>.Failure(PanelOutcome.UnknownOutcome, existing.Message);
        }

        var created = await _panel.CreateClientAsync(
            endpoint,
            BuildRequest(service, inbounds.Select(binding => binding.InboundId).ToList()),
            cancellationToken);

        if (!created.IsSuccess)
        {
            return PanelResult<bool>.Failure(created.Outcome, created.Message);
        }

        var now = _timeProvider.GetUtcNow();

        foreach (var binding in inbounds)
        {
            binding.State = BindingState.Attached;
            binding.LastVerifiedAt = now;
        }

        return PanelResult<bool>.Success(true);
    }

    private async Task<PanelResult<bool>> SetEnabledAsync(
        PanelEndpoint endpoint,
        CustomerService service,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var inbounds = await AttachedInboundsAsync(service, cancellationToken);

        // The panel replaces the client row rather than patching it, so the full field set goes with
        // every update. Sending only `enable` would wipe the quota and expiry.
        var request = BuildRequest(service, inbounds) with { Enabled = enabled };

        var updated = await _panel.UpdateClientAsync(endpoint, request, cancellationToken);

        return updated.IsSuccess
            ? PanelResult<bool>.Success(true)
            : PanelResult<bool>.Failure(updated.Outcome, updated.Message);
    }

    private async Task<PanelResult<bool>> UpdateTermsAsync(
        PanelEndpoint endpoint,
        CustomerService service,
        CancellationToken cancellationToken)
    {
        var inbounds = await AttachedInboundsAsync(service, cancellationToken);

        var updated = await _panel.UpdateClientAsync(
            endpoint, BuildRequest(service, inbounds), cancellationToken);

        return updated.IsSuccess
            ? PanelResult<bool>.Success(true)
            : PanelResult<bool>.Failure(updated.Outcome, updated.Message);
    }

    private async Task<PanelResult<bool>> ResetTrafficAsync(
        PanelEndpoint endpoint,
        CustomerService service,
        CancellationToken cancellationToken) =>
        await _panel.ResetTrafficAsync(endpoint, service.PanelClientEmail!, cancellationToken);

    private async Task<PanelResult<bool>> DecommissionAsync(
        PanelEndpoint endpoint,
        CustomerService service,
        Guid serverId,
        CancellationToken cancellationToken)
    {
        // keepTraffic is false: the service is ending, so the usage record on the panel has no
        // further purpose. A migration is the case that keeps it, and that is a different job.
        var deleted = await _panel.DeleteClientAsync(
            endpoint, service.PanelClientEmail!, keepTraffic: false, cancellationToken);

        if (!deleted.IsSuccess)
        {
            return deleted;
        }

        var now = _timeProvider.GetUtcNow();

        foreach (var binding in await _db.ServiceInboundBindings
                     .Where(binding => binding.ServiceId == service.Id && binding.ServerId == serverId)
                     .ToListAsync(cancellationToken))
        {
            binding.State = BindingState.Detached;
            binding.LastVerifiedAt = now;
        }

        // Released only now, when the client is confirmed gone. Releasing earlier would let another
        // order take the slot while a client still occupied it.
        await _capacity.ReleaseAsync(serverId, cancellationToken);

        return PanelResult<bool>.Success(true);
    }

    // ------------------------------------------------------------------------- outcomes ----

    private async Task SucceedAsync(
        ProvisioningJob job,
        CustomerService service,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        service.LastError = null;

        service.Status = job.Kind switch
        {
            ProvisioningJobKind.Provision => CustomerServiceStatus.Active,
            ProvisioningJobKind.Suspend => CustomerServiceStatus.Suspended,
            ProvisioningJobKind.Resume => CustomerServiceStatus.Active,
            ProvisioningJobKind.Decommission => CustomerServiceStatus.Ended,

            // A renewal or a traffic reset does not itself decide the status — the manager already
            // set it, and overwriting it here would undo an expiry the sweep had just recorded.
            _ => service.Status,
        };

        if (job.Kind == ProvisioningJobKind.Provision)
        {
            await _audit.RecordAsync(
                AuditEntry.For(VpnAuditActions.ServiceProvisioned, nameof(CustomerService), service.Id) with
                {
                    Metadata = AuditMetadata.Create()
                        .Set("userId", service.UserId)
                        .Set("serverId", job.TargetServerId),
                },
                cancellationToken);
        }

        await FinishAsync(job, ProvisioningJobStatus.Succeeded, null, cancellationToken);
    }

    /// <summary>
    /// Parks a job whose outcome nobody knows.
    /// <para>
    /// Not a failure and not a success. The service is flagged so an operator sees it, and the job is
    /// left where the reconciliation sweep will read the panel before anything else acts.
    /// </para>
    /// </summary>
    private async Task ParkForReconciliationAsync(
        ProvisioningJob job,
        CustomerService service,
        string? message,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Job {JobId} ({Kind}) for service {ServiceId} ended without a usable answer. "
            + "Parked for reconciliation; it will not be retried.",
            job.Id,
            job.Kind,
            service.Id);

        service.Status = CustomerServiceStatus.NeedsAttention;
        service.LastError = Truncate(message ?? "The panel did not answer.");

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.ServiceNeedsAttention, nameof(CustomerService), service.Id) with
            {
                Result = AuditResult.Failure,
                Metadata = AuditMetadata.Create()
                    .Set("jobKind", job.Kind)
                    .Set("reason", "unknownOutcome"),
            },
            cancellationToken);

        await FinishAsync(job, ProvisioningJobStatus.NeedsReconciliation, message, cancellationToken);
    }

    private async Task FailRetryablyAsync(
        ProvisioningJob job,
        CustomerService service,
        string? message,
        CancellationToken cancellationToken)
    {
        service.LastError = Truncate(message ?? "The panel refused the request.");

        if (job.Attempts >= ProvisioningJob.MaxAttempts)
        {
            service.Status = CustomerServiceStatus.NeedsAttention;

            _logger.LogError(
                "Job {JobId} ({Kind}) for service {ServiceId} gave up after {Attempts} attempts: {Reason}",
                job.Id,
                job.Kind,
                service.Id,
                job.Attempts,
                service.LastError);

            await FinishAsync(job, ProvisioningJobStatus.Abandoned, message, cancellationToken);
            return;
        }

        // Exponential backoff, so a panel that is briefly refusing is not hammered.
        var delay = TimeSpan.FromSeconds(Math.Pow(2, job.Attempts) * 15);

        job.Status = ProvisioningJobStatus.Failed;
        job.NextAttemptAt = _timeProvider.GetUtcNow().Add(delay);
        job.LastError = Truncate(message);

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task FailPermanentlyAsync(
        ProvisioningJob job,
        CustomerService service,
        string message,
        CancellationToken cancellationToken)
    {
        service.Status = CustomerServiceStatus.NeedsAttention;
        service.LastError = Truncate(message);

        await FinishAsync(job, ProvisioningJobStatus.Abandoned, message, cancellationToken);
    }

    private async Task FinishAsync(
        ProvisioningJob job,
        ProvisioningJobStatus status,
        string? message,
        CancellationToken cancellationToken)
    {
        job.Status = status;
        job.CompletedAt = _timeProvider.GetUtcNow();
        job.LastError = Truncate(message);

        await _db.SaveChangesAsync(cancellationToken);
    }

    // ------------------------------------------------------------------------- helpers ----

    /// <summary>
    /// The client payload for this service.
    /// <para>
    /// Built from the service row alone. No caller supplies a quota, an expiry, an IP limit or an
    /// inbound — and there is no overload that would let them.
    /// </para>
    /// </summary>
    private static PanelClientRequest BuildRequest(
        CustomerService service,
        IReadOnlyList<int> inboundIds) =>
        new(
            service.PanelClientEmail!,
            inboundIds,
            service.TrafficBytes,
            service.ExpiresAt,
            service.DeviceLimit,
            Enabled: service.Status != CustomerServiceStatus.Suspended);

    private async Task<IReadOnlyList<int>> AttachedInboundsAsync(
        CustomerService service,
        CancellationToken cancellationToken)
    {
        var inbounds = await _db.ServiceInboundBindings
            .AsNoTracking()
            .Where(binding => binding.ServiceId == service.Id
                              && binding.ServerId == service.ServerId
                              && binding.State != BindingState.Detached)
            .Select(binding => binding.InboundId)
            .ToListAsync(cancellationToken);

        return inbounds;
    }

    private static string? Truncate(string? value) =>
        value is null
            ? null
            : value.Length <= ProvisioningJob.ErrorMaxLength
                ? value
                : value[..ProvisioningJob.ErrorMaxLength];
}
