using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentinel.Application.Auditing;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Panel;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Servers;

namespace Sentinel.Vpn.Provisioning;

/// <summary>
/// Establishes what is actually true on a panel, then acts.
/// <para>
/// This is the counterpart to never retrying an unknown outcome. When a write ends without an answer,
/// the portal's record and the panel's reality may disagree, and nothing may guess which. So this
/// sweep <b>reads first</b> — one cheap lookup per parked service — and only then decides whether the
/// operation still needs doing.
/// </para>
/// <para>
/// It also syncs usage for healthy services, because the same read answers both questions: the panel
/// returns the client's counters, so asking "does it exist" and "how much has it used" is one call.
/// </para>
/// </summary>
public interface IReconciliationService
{
    /// <summary>Resolves services parked as <see cref="CustomerServiceStatus.NeedsAttention"/>.</summary>
    Task<int> ReconcileAsync(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pulls traffic counters for active services, and applies the consequences: a service past its
    /// quota or its expiry stops being usable.
    /// </summary>
    Task<int> SyncUsageAsync(int batchSize, CancellationToken cancellationToken = default);
}

public sealed class ReconciliationService : IReconciliationService
{
    private readonly IVpnDbContext _db;
    private readonly IThreeXUiClient _panel;
    private readonly IVpnServerAdminService _servers;
    private readonly ICapacityService _capacity;
    private readonly IAuditService _audit;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReconciliationService> _logger;

    public ReconciliationService(
        IVpnDbContext db,
        IThreeXUiClient panel,
        IVpnServerAdminService servers,
        ICapacityService capacity,
        IAuditService audit,
        TimeProvider timeProvider,
        ILogger<ReconciliationService> logger)
    {
        _db = db;
        _panel = panel;
        _servers = servers;
        _capacity = capacity;
        _audit = audit;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> ReconcileAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var parked = await _db.CustomerServices
            .Where(service => service.Status == CustomerServiceStatus.NeedsAttention
                              && service.ServerId != null
                              && service.PanelClientEmail != null)
            .OrderBy(service => service.UpdatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var resolved = 0;

        foreach (var service in parked)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ReconcileOneAsync(service, cancellationToken))
            {
                resolved++;
            }
        }

        return resolved;
    }

    private async Task<bool> ReconcileOneAsync(
        CustomerService service,
        CancellationToken cancellationToken)
    {
        var endpoint = await _servers.ResolveEndpointAsync(service.ServerId!.Value, cancellationToken);

        if (endpoint is null)
        {
            // Cannot look, so cannot decide. Left parked rather than guessed at.
            return false;
        }

        var client = await _panel.GetClientAsync(endpoint, service.PanelClientEmail!, cancellationToken);

        // Still no usable answer. The panel is down or unreachable; try again next sweep.
        if (client.Outcome == PanelOutcome.UnknownOutcome)
        {
            return false;
        }

        // The last unfinished job tells us what was being attempted, which is what makes the panel's
        // answer actionable: "no client" means opposite things for a create and a delete.
        var pending = await _db.ProvisioningJobs
            .Where(job => job.ServiceId == service.Id
                          && job.Status == ProvisioningJobStatus.NeedsReconciliation)
            .OrderByDescending(job => job.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var intent = pending?.Kind ?? ProvisioningJobKind.Provision;
        var existsOnPanel = client.IsSuccess;
        var now = _timeProvider.GetUtcNow();

        string resolution;

        if (intent == ProvisioningJobKind.Decommission)
        {
            if (existsOnPanel)
            {
                // The delete did not land. Re-queued, and safe to: the client is confirmed present, so
                // deleting it is not a repeat of something that already happened.
                Requeue(service, ProvisioningJobKind.Decommission, now);
                resolution = "decommissionRequeued";
            }
            else
            {
                // It did land — the answer was simply lost. Finish the bookkeeping the job never got
                // to, including the capacity slot.
                service.Status = CustomerServiceStatus.Ended;
                service.LastError = null;

                await MarkBindingsAsync(service, BindingState.Detached, now, cancellationToken);
                await _capacity.ReleaseAsync(service.ServerId.Value, cancellationToken);

                resolution = "decommissionConfirmed";
            }
        }
        else if (existsOnPanel)
        {
            // The write did land. Adopt the client rather than creating a second one — this is the
            // duplicate that blind retrying would have produced.
            service.Status = service.Status == CustomerServiceStatus.NeedsAttention
                ? CustomerServiceStatus.Active
                : service.Status;

            service.LastError = null;
            service.UsedBytes = 0;

            await MarkBindingsAsync(service, BindingState.Attached, now, cancellationToken);

            // The terms are pushed again, because a create whose answer was lost may have applied
            // only partially — and an update is idempotent where a create is not.
            Requeue(service, ProvisioningJobKind.UpdateTerms, now);

            resolution = "adoptedExistingClient";
        }
        else
        {
            // Nothing on the panel, so the write certainly did not land. Now it is safe to retry.
            service.Status = CustomerServiceStatus.Pending;
            service.LastError = null;

            await MarkBindingsAsync(service, BindingState.Pending, now, cancellationToken);
            Requeue(service, ProvisioningJobKind.Provision, now);

            resolution = "reprovisionQueued";
        }

        if (pending is not null)
        {
            // Closed off so the same question is not asked again next sweep.
            pending.Status = ProvisioningJobStatus.Abandoned;
            pending.CompletedAt = now;
        }

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.ServiceReconciled, nameof(CustomerService), service.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("intent", intent)
                    .Set("existedOnPanel", existsOnPanel)
                    .Set("resolution", resolution),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Reconciled service {ServiceId}: intent {Intent}, panel {Presence} → {Resolution}.",
            service.Id,
            intent,
            existsOnPanel ? "has the client" : "does not have the client",
            resolution);

        return true;
    }

    public async Task<int> SyncUsageAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        // Suspended services are included: a suspended client still has counters, and knowing where
        // it stands matters when it is resumed. Ended ones are not — there is nothing to ask about.
        var services = await _db.CustomerServices
            .Where(service => service.ServerId != null
                              && service.PanelClientEmail != null
                              && (service.Status == CustomerServiceStatus.Active
                                  || service.Status == CustomerServiceStatus.Suspended
                                  || service.Status == CustomerServiceStatus.Exhausted))
            .OrderBy(service => service.LastUsageSyncAt ?? DateTimeOffset.MinValue)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var synced = 0;

        foreach (var service in services)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var endpoint = await _servers.ResolveEndpointAsync(service.ServerId!.Value, cancellationToken);

            if (endpoint is null)
            {
                continue;
            }

            var traffic = await _panel.GetTrafficAsync(
                endpoint, service.PanelClientEmail!, cancellationToken);

            if (!traffic.IsSuccess)
            {
                // A read that failed changes nothing. Stamping the sync time anyway would make a dead
                // panel look like a quiet customer.
                if (traffic.Outcome == PanelOutcome.NotFound)
                {
                    // The client is gone from the panel but the portal still thinks it is live. That is
                    // a real divergence, so it goes to an operator rather than being papered over.
                    service.Status = CustomerServiceStatus.NeedsAttention;
                    service.LastError = "The client is no longer present on the panel.";

                    await _audit.RecordAsync(
                        AuditEntry.For(
                            VpnAuditActions.ServiceNeedsAttention,
                            nameof(CustomerService),
                            service.Id) with
                        {
                            Result = AuditResult.Failure,
                            Metadata = AuditMetadata.Create().Set("reason", "missingOnPanel"),
                        },
                        cancellationToken);

                    await _db.SaveChangesAsync(cancellationToken);
                }

                continue;
            }

            ApplyUsage(service, traffic.Value!, now);

            await _db.SaveChangesAsync(cancellationToken);
            synced++;
        }

        return synced;
    }

    /// <summary>
    /// Records the panel's counters and applies what they imply.
    /// <para>
    /// The panel is the authority on usage — it is the only thing that sees the traffic — so its
    /// figure replaces ours rather than being added to it. Both consequences are evaluated here so a
    /// service that has run out stops reading as active between sweeps.
    /// </para>
    /// </summary>
    private static void ApplyUsage(
        CustomerService service,
        PanelClientTraffic traffic,
        DateTimeOffset now)
    {
        service.UsedBytes = traffic.UsedBytes;
        service.LastUsageSyncAt = now;
        service.LastOnlineAt = traffic.LastOnlineAt ?? service.LastOnlineAt;

        // A suspended service stays suspended: an operator withheld it, and running out of quota does
        // not change that decision.
        if (service.Status == CustomerServiceStatus.Suspended)
        {
            return;
        }

        var expired = service.ExpiresAt is { } expires && expires <= now;
        var exhausted = !service.IsUnlimitedTraffic && service.UsedBytes >= service.TrafficBytes;

        // Expiry is reported ahead of exhaustion when both apply: a renewal fixes expiry, whereas a
        // top-up fixes quota, and telling a customer the wrong one sends them down the wrong path.
        service.Status = expired
            ? CustomerServiceStatus.Expired
            : exhausted
                ? CustomerServiceStatus.Exhausted
                : CustomerServiceStatus.Active;
    }

    private void Requeue(CustomerService service, ProvisioningJobKind kind, DateTimeOffset now) =>
        _db.ProvisioningJobs.Add(new ProvisioningJob
        {
            Id = SequentialGuid.New(now),
            ServiceId = service.Id,
            Kind = kind,
            Status = ProvisioningJobStatus.Pending,
            NextAttemptAt = now,
            TargetServerId = service.ServerId,
        });

    private async Task MarkBindingsAsync(
        CustomerService service,
        BindingState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var bindings = await _db.ServiceInboundBindings
            .Where(binding => binding.ServiceId == service.Id
                              && binding.ServerId == service.ServerId)
            .ToListAsync(cancellationToken);

        foreach (var binding in bindings)
        {
            binding.State = state;

            // Only a confirmed observation stamps the verification time. Marking a re-queued binding
            // as "verified just now" would make a guess look like a fact.
            if (state is BindingState.Attached or BindingState.Detached)
            {
                binding.LastVerifiedAt = now;
            }
        }
    }
}
