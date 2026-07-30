using Microsoft.EntityFrameworkCore;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Auditing;
using Sentinel.Application.Common;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Panel;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Provisioning;
using Sentinel.Vpn.Servers;

namespace Sentinel.Vpn.Migration;

/// <summary>
/// Plans migrations and computes their terms.
/// <para>
/// Everything that decides what the customer ends up with happens here, once, before any panel is
/// written to: the destination is chosen, capacity is reserved on it, the remaining allowance is read
/// from the source, and the expiry is copied across unchanged. The executor that follows only carries
/// those decisions out.
/// </para>
/// </summary>
public sealed class ServiceMigrationManager : IServiceMigrationManager
{
    private readonly IVpnDbContext _vpn;
    private readonly ISentinelDbContext _db;
    private readonly IThreeXUiClient _panel;
    private readonly IVpnServerAdminService _servers;
    private readonly ICapacityService _capacity;
    private readonly IAuditService _audit;
    private readonly TimeProvider _timeProvider;

    public ServiceMigrationManager(
        IVpnDbContext vpn,
        ISentinelDbContext db,
        IThreeXUiClient panel,
        IVpnServerAdminService servers,
        ICapacityService capacity,
        IAuditService audit,
        TimeProvider timeProvider)
    {
        _vpn = vpn;
        _db = db;
        _panel = panel;
        _servers = servers;
        _capacity = capacity;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    public async Task<OperationResult<Guid>> PlanAsync(
        MigrateServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var service = await _vpn.CustomerServices
            .FirstOrDefaultAsync(candidate => candidate.Id == request.ServiceId, cancellationToken);

        if (service is null || service.ServerId is null || service.PanelClientEmail is null)
        {
            return OperationResult<Guid>.Failure(MigrationErrors.ServiceNotFound);
        }

        // Only a service with a live client can be moved. An expired or exhausted one has nothing
        // worth carrying over, and a half-provisioned one has nothing to carry.
        if (service.Status is not (CustomerServiceStatus.Active or CustomerServiceStatus.Suspended))
        {
            return OperationResult<Guid>.Failure(MigrationErrors.NotMigratable);
        }

        // One at a time. Two concurrent migrations of the same service would each believe they owned
        // the source client, and the second would delete what the first had just moved.
        var inFlight = await _vpn.ServiceMigrations.AnyAsync(
            candidate => candidate.ServiceId == service.Id
                         && candidate.Step != MigrationStep.Completed
                         && candidate.Step != MigrationStep.Abandoned
                         && candidate.Step != MigrationStep.RolledBack,
            cancellationToken);

        if (inFlight)
        {
            return OperationResult<Guid>.Failure(MigrationErrors.AlreadyInFlight);
        }

        var sourceServerId = service.ServerId.Value;

        var destination = await ChooseDestinationAsync(request, sourceServerId, cancellationToken);

        if (!destination.Succeeded)
        {
            return OperationResult<Guid>.Failure(destination.ErrorKey!);
        }

        var destinationServerId = destination.Value;

        // Read the source panel rather than trusting the cached counter. Usage syncs on a timer, so
        // the stored figure can be a quarter of an hour stale — and on a busy service that is the
        // difference between the customer keeping their remaining gigabytes and losing them.
        var remaining = await ReadRemainingAsync(service, sourceServerId, cancellationToken);

        if (!remaining.Succeeded)
        {
            return OperationResult<Guid>.Failure(remaining.ErrorKey!);
        }

        // Reserved before the row is written, and held until the source client is confirmed gone —
        // so during the migration the customer legitimately occupies a slot on both servers.
        var reservation = await _capacity.ReserveAsync(destinationServerId, cancellationToken);

        if (!reservation.IsSuccess)
        {
            return OperationResult<Guid>.Failure(MigrationErrors.NoCapacity);
        }

        var now = _timeProvider.GetUtcNow();
        var migrationId = SequentialGuid.New(now);

        var migration = new ServiceMigration
        {
            Id = migrationId,
            ServiceId = service.Id,
            SourceServerId = sourceServerId,
            DestinationServerId = destinationServerId,
            Step = MigrationStep.Planned,

            // Both computed above, both frozen now. Nothing downstream recomputes either.
            RemainingBytes = remaining.Value.Remaining,
            SourceUsedBytes = remaining.Value.Used,
            ExpiresAt = service.ExpiresAt,

            NextAttemptAt = now,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
        };

        _vpn.ServiceMigrations.Add(migration);

        // The bindings the destination will get, written as intent first. The service now has
        // bindings on two servers, which is exactly why the binding table is keyed by server.
        foreach (var inboundId in await EnabledInboundsAsync(destinationServerId, cancellationToken))
        {
            _vpn.ServiceInboundBindings.Add(new ServiceInboundBinding
            {
                Id = SequentialGuid.New(now),
                ServiceId = service.Id,
                ServerId = destinationServerId,
                InboundId = inboundId,
                State = BindingState.Pending,
            });
        }

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.ServiceMigrationPlanned, nameof(ServiceMigration), migrationId) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("serviceId", service.Id)
                    .Set("userId", service.UserId)
                    .Set("sourceServerId", sourceServerId)
                    .Set("destinationServerId", destinationServerId)
                    .Set("remainingBytes", migration.RemainingBytes)
                    .Set("usedBytes", migration.SourceUsedBytes),
            },
            cancellationToken);

        await _vpn.SaveChangesAsync(cancellationToken);

        return OperationResult<Guid>.Success(migrationId);
    }

    public async Task<OperationResult> RollBackAsync(
        Guid migrationId,
        CancellationToken cancellationToken = default)
    {
        var migration = await _vpn.ServiceMigrations
            .FirstOrDefaultAsync(candidate => candidate.Id == migrationId, cancellationToken);

        if (migration is null)
        {
            return OperationResult.Failure(MigrationErrors.ServiceNotFound);
        }

        // Only while the source is untouched. Once the destination is verified the migration is
        // committed: rolling back from there would mean deleting a client that may by then be the
        // customer's only working one.
        if (migration.Step is not (MigrationStep.Planned or MigrationStep.Creating
            or MigrationStep.Verifying))
        {
            return OperationResult.Failure(MigrationErrors.NotRollbackable);
        }

        var service = await _vpn.CustomerServices
            .FirstOrDefaultAsync(candidate => candidate.Id == migration.ServiceId, cancellationToken);

        var now = _timeProvider.GetUtcNow();

        // Anything already created at the destination is removed. keepTraffic is false: this client
        // never served the customer, so its counters are noise.
        if (migration.Step is MigrationStep.Creating or MigrationStep.Verifying
            && service?.PanelClientEmail is { } email)
        {
            var endpoint = await _servers.ResolveEndpointAsync(
                migration.DestinationServerId, cancellationToken);

            if (endpoint is not null)
            {
                var deleted = await _panel.DeleteClientAsync(
                    endpoint, email, keepTraffic: false, cancellationToken);

                // An unknown outcome here must not be papered over: something may still be sitting on
                // the destination panel, and only reconciliation may decide.
                if (deleted.Outcome == PanelOutcome.UnknownOutcome)
                {
                    migration.Step = MigrationStep.NeedsAttention;
                    migration.LastError = Truncate(deleted.Message ?? "The panel did not answer.");

                    await _vpn.SaveChangesAsync(cancellationToken);

                    return OperationResult.Failure(MigrationErrors.DestinationUnusable);
                }
            }
        }

        await ReleaseDestinationAsync(migration, now, cancellationToken);

        migration.Step = MigrationStep.RolledBack;
        migration.CompletedAt = now;
        migration.LastError = null;

        await _audit.RecordAsync(
            AuditEntry.For(
                VpnAuditActions.ServiceMigrationRolledBack, nameof(ServiceMigration), migration.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("serviceId", migration.ServiceId)
                    .Set("destinationServerId", migration.DestinationServerId),
            },
            cancellationToken);

        await _vpn.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<IReadOnlyList<MigrationView>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _vpn.ServiceMigrations
            .AsNoTracking()
            .OrderByDescending(migration => migration.CreatedAt)
            .Select(migration => new
            {
                migration.Id,
                migration.ServiceId,
                migration.SourceServerId,
                migration.DestinationServerId,
                migration.Step,
                migration.RemainingBytes,
                migration.ExpiresAt,
                migration.Attempts,
                migration.DualActiveSince,
                migration.CompletedAt,
                migration.LastError,
                migration.CreatedAt,
                UserId = migration.Service!.UserId,
                PlanNameEn = migration.Service.PlanNameEn,
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var serverIds = rows
            .SelectMany(row => new[] { row.SourceServerId, row.DestinationServerId })
            .Distinct()
            .ToList();

        var servers = await _vpn.VpnServers
            .AsNoTracking()
            .Where(server => serverIds.Contains(server.Id))
            .Select(server => new { server.Id, server.Key })
            .ToListAsync(cancellationToken);

        var serverKeys = servers.ToDictionary(server => server.Id, server => server.Key);

        var userIds = rows.Select(row => row.UserId).Distinct().ToList();

        // One extra query rather than a join across the module boundary, matched in memory.
        var users = await _db.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new { user.Id, user.UserName })
            .ToListAsync(cancellationToken);

        var userNames = users.ToDictionary(user => user.Id, user => user.UserName ?? "—");

        return rows
            .Select(row => new MigrationView(
                row.Id,
                row.ServiceId,
                userNames.GetValueOrDefault(row.UserId, "—"),
                row.PlanNameEn,
                row.SourceServerId,
                serverKeys.GetValueOrDefault(row.SourceServerId),
                row.DestinationServerId,
                serverKeys.GetValueOrDefault(row.DestinationServerId),
                row.Step,
                row.RemainingBytes,
                row.ExpiresAt,
                row.Attempts,
                row.DualActiveSince,
                row.CompletedAt,
                row.LastError,
                row.CreatedAt))
            .ToList();
    }

    public async Task<MigrationView?> ActiveForServiceAsync(
        Guid serviceId,
        CancellationToken cancellationToken = default)
    {
        var all = await ListAsync(cancellationToken);

        return all.FirstOrDefault(
            migration => migration.ServiceId == serviceId && !migration.IsFinished);
    }

    // -------------------------------------------------------------------------- helpers ----

    /// <summary>
    /// Resolves the destination, either the one an operator named or the best in a country.
    /// <para>
    /// A named server still has to pass the same checks the selector applies. Naming one is a
    /// preference, not an override — an operator should not be able to place a customer on a server
    /// with no usable inbound simply by picking it from a list.
    /// </para>
    /// </summary>
    private async Task<OperationResult<Guid>> ChooseDestinationAsync(
        MigrateServiceRequest request,
        Guid sourceServerId,
        CancellationToken cancellationToken)
    {
        var candidates = await LoadCandidatesAsync(cancellationToken);

        if (request.DestinationServerId is { } named)
        {
            if (named == sourceServerId)
            {
                return OperationResult<Guid>.Failure(MigrationErrors.SameServer);
            }

            var candidate = candidates.FirstOrDefault(server => server.ServerId == named);

            if (candidate is null)
            {
                return OperationResult<Guid>.Failure(MigrationErrors.DestinationNotFound);
            }

            // Same predicates as ServerSelector's stages, applied to the single named server.
            var usable = candidate.Status == VpnServerStatus.Active
                         && candidate.Health != VpnServerHealth.Unreachable
                         && candidate.EnabledInboundCount > 0;

            if (!usable)
            {
                return OperationResult<Guid>.Failure(MigrationErrors.DestinationUnusable);
            }

            return candidate.RemainingCapacity > 0
                ? OperationResult<Guid>.Success(named)
                : OperationResult<Guid>.Failure(MigrationErrors.NoCapacity);
        }

        // The source is excluded before selection rather than rejected after it: otherwise a country
        // with one healthy server would report "same server" instead of "nowhere else to go".
        var elsewhere = candidates.Where(server => server.ServerId != sourceServerId).ToList();

        var selection = ServerSelector.Select(elsewhere, request.CountryCode);

        if (!selection.IsSuccess)
        {
            return OperationResult<Guid>.Failure(selection.Outcome switch
            {
                SelectionOutcome.NoCapacity => MigrationErrors.NoCapacity,
                _ => MigrationErrors.DestinationUnusable,
            });
        }

        return OperationResult<Guid>.Success(selection.Server!.ServerId);
    }

    /// <summary>
    /// Reads the source panel for what the customer has left.
    /// <para>
    /// Unlimited stays unlimited: the panel expresses no limit as zero, and subtracting usage from
    /// zero would migrate an unlimited service onto a quota of nothing.
    /// </para>
    /// </summary>
    private async Task<OperationResult<(long Remaining, long Used)>> ReadRemainingAsync(
        CustomerService service,
        Guid sourceServerId,
        CancellationToken cancellationToken)
    {
        if (service.IsUnlimitedTraffic)
        {
            return OperationResult<(long, long)>.Success((0, service.UsedBytes));
        }

        var endpoint = await _servers.ResolveEndpointAsync(sourceServerId, cancellationToken);

        if (endpoint is null)
        {
            return OperationResult<(long, long)>.Failure(MigrationErrors.SourceUnreadable);
        }

        var traffic = await _panel.GetTrafficAsync(
            endpoint, service.PanelClientEmail!, cancellationToken);

        if (!traffic.IsSuccess)
        {
            // Refused outright rather than falling back to the cached counter. Guessing the number
            // that becomes the customer's new allowance is not a reasonable failure mode.
            return OperationResult<(long, long)>.Failure(MigrationErrors.SourceUnreadable);
        }

        var used = traffic.Value!.UsedBytes;

        // Floored at zero: a service already over its allowance migrates with nothing left, not with
        // a negative quota the panel would read as unlimited.
        var remaining = Math.Max(0, service.TrafficBytes - used);

        return OperationResult<(long, long)>.Success((remaining, used));
    }

    private async Task ReleaseDestinationAsync(
        ServiceMigration migration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var bindings = await _vpn.ServiceInboundBindings
            .Where(binding => binding.ServiceId == migration.ServiceId
                              && binding.ServerId == migration.DestinationServerId)
            .ToListAsync(cancellationToken);

        foreach (var binding in bindings)
        {
            binding.State = BindingState.Detached;
            binding.LastVerifiedAt = now;
        }

        await _capacity.ReleaseAsync(migration.DestinationServerId, cancellationToken);
    }

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

    private static string? Truncate(string? value) =>
        value is null
            ? null
            : value.Length <= ServiceMigration.ErrorMaxLength
                ? value
                : value[..ServiceMigration.ErrorMaxLength];
}
