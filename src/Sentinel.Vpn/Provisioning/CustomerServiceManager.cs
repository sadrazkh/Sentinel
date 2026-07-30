using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Common;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Vpn.Delivery;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Panel;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Servers;

namespace Sentinel.Vpn.Provisioning;

/// <summary>
/// The lifecycle of a customer's VPN service.
/// <para>
/// Records intent and queues a job; it never calls a panel itself. Two reasons, and the second is the
/// important one: a member should not wait on a third-party system, and an intent that lives in the
/// database survives the process dying mid-call — which is exactly when nobody knows whether the call
/// took effect.
/// </para>
/// </summary>
public sealed class CustomerServiceManager : ICustomerServiceManager
{
    private readonly IVpnDbContext _vpn;
    private readonly ISentinelDbContext _db;
    private readonly ICapacityService _capacity;
    private readonly IDeliverySecretProtector _secrets;
    private readonly IAuditService _audit;
    private readonly TimeProvider _timeProvider;

    public CustomerServiceManager(
        IVpnDbContext vpn,
        ISentinelDbContext db,
        ICapacityService capacity,
        IDeliverySecretProtector secrets,
        IAuditService audit,
        TimeProvider timeProvider)
    {
        _vpn = vpn;
        _db = db;
        _capacity = capacity;
        _secrets = secrets;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    public async Task<OperationResult<Guid>> CreateAsync(
        CreateServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await _db.Users.AnyAsync(user => user.Id == request.UserId, cancellationToken))
        {
            return OperationResult<Guid>.Failure(ServiceErrors.MemberNotFound);
        }

        var plan = await _vpn.ServicePlans
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == request.PlanId, cancellationToken);

        if (plan is null)
        {
            return OperationResult<Guid>.Failure(ServiceErrors.PlanNotFound);
        }

        var selection = ServerSelector.Select(
            await LoadCandidatesAsync(cancellationToken), plan.CountryCode);

        if (!selection.IsSuccess)
        {
            return OperationResult<Guid>.Failure(MapSelection(selection.Outcome));
        }

        // Reserved before the row is written, so two simultaneous orders cannot both take the last
        // slot. If this fails the caller is told now rather than at provisioning time.
        var reservation = await _capacity.ReserveAsync(selection.Server!.ServerId, cancellationToken);

        if (!reservation.IsSuccess)
        {
            return OperationResult<Guid>.Failure(
                reservation.Outcome == ReservationOutcome.NoCapacity
                    ? ServiceErrors.NoCapacity
                    : ServiceErrors.NoServerAvailable);
        }

        var now = _timeProvider.GetUtcNow();
        var serviceId = SequentialGuid.New(now);

        // Issued here rather than on first use, so a service always has a link the moment it goes
        // live. The member is never made to press a button to get the thing they bought.
        var (token, hash) = DeliveryToken.Create();

        var service = new CustomerService
        {
            Id = serviceId,
            UserId = request.UserId,
            ProductId = plan.ProductId,
            PlanId = plan.Id,

            // Copied, not referenced. A plan is a price list and changes; what this customer was sold
            // does not.
            PlanNameFa = plan.NameFa,
            PlanNameEn = plan.NameEn,
            TrafficBytes = plan.TrafficBytes,
            DeviceLimit = plan.DeviceLimit,

            ServerId = selection.Server.ServerId,

            // Minted here, never derived from the service id or the member: a derivable identifier on
            // a third-party system is one an outsider can compute.
            PanelClientEmail = PanelClientEmail.Create(),

            DeliveryTokenHash = hash,
            DeliveryTokenSealed = _secrets.Seal(token),
            DeliveryTokenIssuedAt = now,

            Status = CustomerServiceStatus.Pending,
            StartsAt = now,
            ExpiresAt = now.AddDays(plan.DurationDays),
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
        };

        _vpn.CustomerServices.Add(service);

        // The bindings the provisioning job will attempt. Written as intent first so a crash between
        // here and the panel call leaves a record of what was meant to happen.
        foreach (var inboundId in await EnabledInboundsAsync(selection.Server.ServerId, cancellationToken))
        {
            _vpn.ServiceInboundBindings.Add(new ServiceInboundBinding
            {
                Id = SequentialGuid.New(now),
                ServiceId = serviceId,
                ServerId = selection.Server.ServerId,
                InboundId = inboundId,
                State = BindingState.Pending,
            });
        }

        Enqueue(service, ProvisioningJobKind.Provision, now);

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.ServiceCreated, nameof(CustomerService), serviceId) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("userId", request.UserId)
                    .Set("planSlug", plan.Key)
                    .Set("serverSlug", selection.Server.Key)
                    .Set("trafficBytes", plan.TrafficBytes)
                    .Set("durationDays", plan.DurationDays),
            },
            cancellationToken);

        await _vpn.SaveChangesAsync(cancellationToken);

        return OperationResult<Guid>.Success(serviceId);
    }

    public Task<OperationResult> SuspendAsync(Guid serviceId, CancellationToken cancellationToken = default) =>
        QueueTransitionAsync(
            serviceId,
            ProvisioningJobKind.Suspend,
            CustomerServiceStatus.Suspended,
            VpnAuditActions.ServiceSuspended,
            // Only something currently working can be suspended. Suspending an expired service would
            // record a state change that means nothing.
            allowed: [CustomerServiceStatus.Active],
            cancellationToken);

    public Task<OperationResult> ResumeAsync(Guid serviceId, CancellationToken cancellationToken = default) =>
        QueueTransitionAsync(
            serviceId,
            ProvisioningJobKind.Resume,
            CustomerServiceStatus.Active,
            VpnAuditActions.ServiceResumed,
            allowed: [CustomerServiceStatus.Suspended],
            cancellationToken);

    public async Task<OperationResult> RenewAsync(
        Guid serviceId,
        int additionalDays,
        CancellationToken cancellationToken = default)
    {
        if (additionalDays is < 1 or > 3650)
        {
            return OperationResult.Failure(PlanErrorsBridge.DurationInvalid);
        }

        var service = await LoadForWriteAsync(serviceId, cancellationToken);

        if (service is null)
        {
            return OperationResult.Failure(ServiceErrors.NotFound);
        }

        if (service.Status == CustomerServiceStatus.Ended)
        {
            return OperationResult.Failure(ServiceErrors.AlreadyEnded);
        }

        var now = _timeProvider.GetUtcNow();

        // Extended from whichever is later. Renewing a lapsed service from its old expiry would give
        // the customer days that had already passed.
        var from = service.ExpiresAt is { } expires && expires > now ? expires : now;

        var previous = service.ExpiresAt;
        service.ExpiresAt = from.AddDays(additionalDays);

        // A renewal brings an expired or exhausted service back. The panel is told by the job; the
        // status is moved here so the member's page reflects it immediately.
        if (service.Status is CustomerServiceStatus.Expired or CustomerServiceStatus.Exhausted)
        {
            service.Status = CustomerServiceStatus.Active;
        }

        Enqueue(service, ProvisioningJobKind.UpdateTerms, now);

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.ServiceRenewed, nameof(CustomerService), serviceId) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("addedDays", additionalDays)
                    .SetChange("expiresAt", previous, service.ExpiresAt),
            },
            cancellationToken);

        await _vpn.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ResetTrafficAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        var service = await LoadForWriteAsync(serviceId, cancellationToken);

        if (service is null)
        {
            return OperationResult.Failure(ServiceErrors.NotFound);
        }

        if (service.Status == CustomerServiceStatus.Ended)
        {
            return OperationResult.Failure(ServiceErrors.AlreadyEnded);
        }

        var now = _timeProvider.GetUtcNow();
        var previous = service.UsedBytes;

        // Zeroed locally as well as on the panel: the counter here is what the member's page reads,
        // and leaving it until the next usage sync would show them a full quota they no longer have.
        service.UsedBytes = 0;

        if (service.Status == CustomerServiceStatus.Exhausted)
        {
            service.Status = CustomerServiceStatus.Active;
        }

        Enqueue(service, ProvisioningJobKind.ResetTraffic, now);

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.ServiceTrafficReset, nameof(CustomerService), serviceId) with
            {
                Metadata = AuditMetadata.Create().Set("previousUsedBytes", previous),
            },
            cancellationToken);

        await _vpn.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> DecommissionAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        var service = await LoadForWriteAsync(serviceId, cancellationToken);

        if (service is null)
        {
            return OperationResult.Failure(ServiceErrors.NotFound);
        }

        if (service.Status == CustomerServiceStatus.Ended)
        {
            return OperationResult.Failure(ServiceErrors.AlreadyEnded);
        }

        var now = _timeProvider.GetUtcNow();

        service.Status = CustomerServiceStatus.Decommissioning;

        // The delivery token dies now, not when the panel call completes. Whoever holds that URL
        // should stop being served the moment the service is withdrawn, regardless of how long the
        // panel takes to answer.
        service.DeliveryTokenHash = null;
        service.DeliveryTokenSealed = null;
        service.DeliveryTokenIssuedAt = null;

        Enqueue(service, ProvisioningJobKind.Decommission, now);

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.ServiceDecommissioned, nameof(CustomerService), serviceId) with
            {
                Metadata = AuditMetadata.Create().Set("userId", service.UserId),
            },
            cancellationToken);

        await _vpn.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult<string>> RotateDeliveryTokenAsync(
        Guid serviceId,
        Guid requestingUserId,
        CancellationToken cancellationToken = default)
    {
        // Scoped by owner as well as id, and the two failures are indistinguishable: a member probing
        // another's service id gets "not found", not "forbidden".
        var service = await _vpn.CustomerServices
            .FirstOrDefaultAsync(
                candidate => candidate.Id == serviceId && candidate.UserId == requestingUserId,
                cancellationToken);

        if (service is null)
        {
            return OperationResult<string>.Failure(ServiceErrors.NotFound);
        }

        if (service.Status is CustomerServiceStatus.Ended or CustomerServiceStatus.Decommissioning)
        {
            return OperationResult<string>.Failure(ServiceErrors.AlreadyEnded);
        }

        var (token, hash) = DeliveryToken.Create();

        service.DeliveryTokenHash = hash;
        service.DeliveryTokenSealed = _secrets.Seal(token);
        service.DeliveryTokenIssuedAt = _timeProvider.GetUtcNow();

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.ServiceLinkRotated, nameof(CustomerService), serviceId) with
            {
                // A fingerprint, never the token. This row is readable by operators.
                Metadata = AuditMetadata.Create()
                    .Set("userId", service.UserId)
                    .Set("linkPrefix", DeliveryToken.Fingerprint(token)),
            },
            cancellationToken);

        await _vpn.SaveChangesAsync(cancellationToken);

        return OperationResult<string>.Success(token);
    }

    // ------------------------------------------------------------------------- helpers ----

    private async Task<OperationResult> QueueTransitionAsync(
        Guid serviceId,
        ProvisioningJobKind kind,
        CustomerServiceStatus target,
        string auditAction,
        CustomerServiceStatus[] allowed,
        CancellationToken cancellationToken)
    {
        var service = await LoadForWriteAsync(serviceId, cancellationToken);

        if (service is null)
        {
            return OperationResult.Failure(ServiceErrors.NotFound);
        }

        if (!allowed.Contains(service.Status))
        {
            return OperationResult.Failure(
                service.Status == CustomerServiceStatus.Ended
                    ? ServiceErrors.AlreadyEnded
                    : ServiceErrors.BusyProvisioning);
        }

        var now = _timeProvider.GetUtcNow();

        service.Status = target;
        Enqueue(service, kind, now);

        await _audit.RecordAsync(
            AuditEntry.For(auditAction, nameof(CustomerService), serviceId) with
            {
                Metadata = AuditMetadata.Create().Set("userId", service.UserId),
            },
            cancellationToken);

        await _vpn.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    /// <summary>
    /// Adds a job.
    /// <para>
    /// The target server is captured now rather than read at run time: a migration changes the
    /// service's server, and a job queued beforehand must still act on the panel it was written for
    /// — otherwise a queued decommission would delete the client from its new home.
    /// </para>
    /// </summary>
    private void Enqueue(CustomerService service, ProvisioningJobKind kind, DateTimeOffset now) =>
        _vpn.ProvisioningJobs.Add(new ProvisioningJob
        {
            Id = SequentialGuid.New(now),
            ServiceId = service.Id,
            Kind = kind,
            Status = ProvisioningJobStatus.Pending,

            // In the past, so the next sweep picks it up rather than waiting a cycle.
            NextAttemptAt = now,
            TargetServerId = service.ServerId,
        });

    private Task<CustomerService?> LoadForWriteAsync(Guid serviceId, CancellationToken cancellationToken) =>
        _vpn.CustomerServices
            .FirstOrDefaultAsync(candidate => candidate.Id == serviceId, cancellationToken);

    private async Task<IReadOnlyList<ServerCandidate>> LoadCandidatesAsync(
        CancellationToken cancellationToken) =>
        await _vpn.VpnServers
            .AsNoTracking()
            .Select(server => new ServerCandidate(
                server.Id,
                server.Key,
                server.CountryCode,
                server.Status,
                server.Health,
                server.MaxClients,
                server.ReservedClients,
                server.SelectionPriority,
                server.InboundProfiles.Count(profile => profile.IsEnabled)))
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyList<int>> EnabledInboundsAsync(
        Guid serverId,
        CancellationToken cancellationToken) =>
        await _vpn.ServerInboundProfiles
            .AsNoTracking()
            .Where(profile => profile.ServerId == serverId && profile.IsEnabled)
            .OrderBy(profile => profile.DisplayOrder)
            .Select(profile => profile.InboundId)
            .ToListAsync(cancellationToken);

    private static string MapSelection(SelectionOutcome outcome) => outcome switch
    {
        SelectionOutcome.NoCapacity => ServiceErrors.NoCapacity,
        SelectionOutcome.NoUsableInbound => ServiceErrors.NoUsableInbound,
        _ => ServiceErrors.NoServerAvailable,
    };
}

/// <summary>
/// Reuses the plan module's duration message rather than inventing a second one that says the same
/// thing in different words.
/// </summary>
internal static class PlanErrorsBridge
{
    public const string DurationInvalid = Plans.PlanErrors.DurationInvalid;
}
