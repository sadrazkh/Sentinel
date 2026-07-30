using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Vpn.Persistence;
using Sentinel.Application.Auditing;
using Sentinel.Application.Catalog;
using Sentinel.Application.Common;
using Sentinel.Domain.Auditing;
using Sentinel.Domain.Common;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Panel;

namespace Sentinel.Vpn.Servers;

/// <summary>
/// Managing the panels the portal provisions against.
/// <para>
/// The one place a panel credential is written, and the only place it is decrypted for a call.
/// Nothing here ever returns a token to a caller — <see cref="ResolveEndpointAsync"/> hands out an
/// endpoint that goes straight to the client, and the admin projections carry only a hint.
/// </para>
/// </summary>
public sealed class VpnServerAdminService : IVpnServerAdminService
{
    private readonly IVpnDbContext _db;
    private readonly IThreeXUiClient _panel;
    private readonly IPanelCredentialProtector _protector;
    private readonly IAuditService _audit;
    private readonly ThreeXUiOptions _options;
    private readonly TimeProvider _timeProvider;

    public VpnServerAdminService(
        IVpnDbContext db,
        IThreeXUiClient panel,
        IPanelCredentialProtector protector,
        IAuditService audit,
        IOptions<ThreeXUiOptions> options,
        TimeProvider timeProvider)
    {
        _db = db;
        _panel = panel;
        _protector = protector;
        _audit = audit;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task<OperationResult<Guid>> SaveAsync(
        Guid? serverId,
        VpnServerSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = request.Key.Trim().ToLowerInvariant();

        if (!ApplicationKey.IsValid(key))
        {
            return OperationResult<Guid>.Failure(VpnServerErrors.KeyInvalid);
        }

        if (!PanelBaseUrlPolicy.IsAllowed(request.BaseUrl, _options.AllowInsecurePanelUrls))
        {
            return OperationResult<Guid>.Failure(VpnServerErrors.BaseUrlInvalid);
        }

        var country = request.CountryCode.Trim().ToUpperInvariant();

        if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
        {
            return OperationResult<Guid>.Failure(VpnServerErrors.CountryInvalid);
        }

        var isNew = serverId is null;
        VpnServer server;

        if (serverId is { } id)
        {
            var existing = await _db.VpnServers
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

            if (existing is null)
            {
                return OperationResult<Guid>.Failure(VpnServerErrors.NotFound);
            }

            if (request.ConcurrencyToken is { } token && existing.ConcurrencyToken != token)
            {
                return OperationResult<Guid>.Failure(OperationErrors.ConcurrencyConflict);
            }

            server = existing;
        }
        else
        {
            // A brand-new server has no credential yet, so one is required.
            if (string.IsNullOrWhiteSpace(request.ApiToken))
            {
                return OperationResult<Guid>.Failure(VpnServerErrors.TokenRequired);
            }

            server = new VpnServer
            {
                Id = SequentialGuid.New(_timeProvider.GetUtcNow()),
                Key = key,
            };

            _db.VpnServers.Add(server);
        }

        if (await _db.VpnServers
                .AnyAsync(s => s.Key == key && s.Id != server.Id, cancellationToken))
        {
            return OperationResult<Guid>.Failure(VpnServerErrors.KeyTaken);
        }

        var addressChanged = !string.Equals(server.BaseUrl, request.BaseUrl.Trim(), StringComparison.Ordinal);
        var tokenChanged = !string.IsNullOrWhiteSpace(request.ApiToken);

        server.Key = key;
        server.NameFa = request.NameFa.Trim();
        server.NameEn = request.NameEn.Trim();
        server.CountryCode = country;
        server.BaseUrl = request.BaseUrl.Trim();
        server.Status = request.Status;
        server.MaxClients = Math.Max(0, request.MaxClients);
        server.SelectionPriority = request.SelectionPriority;
        server.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();

        if (tokenChanged)
        {
            var token = request.ApiToken!.Trim();

            server.EncryptedApiToken = _protector.Protect(token);
            server.ApiTokenHint = IPanelCredentialProtector.HintFor(token);
        }

        // A changed address or credential invalidates what we knew: the next probe decides again.
        // Leaving a stale "healthy" in place would let selection place a service on a panel that
        // has not been reached since it was reconfigured.
        if (addressChanged || tokenChanged)
        {
            server.Health = VpnServerHealth.Unknown;
            server.LastHealthCheckAt = null;
            server.LastHealthError = null;

            if (server.Status == VpnServerStatus.Active)
            {
                server.Status = VpnServerStatus.Unverified;
            }
        }

        await _audit.RecordAsync(
            AuditEntry.For(
                isNew ? VpnAuditActions.ServerCreated : VpnAuditActions.ServerUpdated,
                nameof(VpnServer),
                server.Id) with
            {
                // The address host and whether the credential changed — never the credential, and
                // never the full URL, which can carry a base path an operator treats as secret.
                //
                // The flag is named around none of the guard's forbidden fragments — "token",
                // "credential" and "secret" are all refused outright, and the guard is right to
                // do that: a name-based rule that accepts exceptions stops being a rule.
                Metadata = AuditMetadata.Create()
                    .Set("serverSlug", server.Key)
                    .Set("country", server.CountryCode)
                    .Set("host", HostOf(server.BaseUrl))
                    .Set("status", server.Status)
                    .Set("panelAuthReplaced", tokenChanged),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult<Guid>.Success(server.Id);
    }

    public async Task<OperationResult<ServerProbeResult>> ProbeAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await _db.VpnServers
            .FirstOrDefaultAsync(s => s.Id == serverId, cancellationToken);

        if (server is null)
        {
            return OperationResult<ServerProbeResult>.Failure(VpnServerErrors.NotFound);
        }

        var endpoint = EndpointFor(server);

        if (endpoint is null)
        {
            await RecordUnreachableAsync(server, "The stored token could not be decrypted.", cancellationToken);

            return OperationResult<ServerProbeResult>.Failure(VpnServerErrors.TokenUnreadable);
        }

        var status = await _panel.GetStatusAsync(endpoint, cancellationToken);

        if (!status.IsSuccess)
        {
            await RecordUnreachableAsync(server, Describe(status.Outcome, status.Message), cancellationToken);

            return OperationResult<ServerProbeResult>.Success(new ServerProbeResult(
                false, VpnServerHealth.Unreachable, Describe(status.Outcome, status.Message), false, null, 0));
        }

        var inbounds = await _panel.ListInboundsAsync(endpoint, cancellationToken);
        var inboundCount = inbounds.IsSuccess ? inbounds.Value!.Count : 0;

        // Reachable but Xray stopped is "degraded", not healthy: the panel answers, yet no
        // customer traffic flows. Selection treats it as unusable while an operator investigates.
        var health = status.Value!.XrayRunning ? VpnServerHealth.Healthy : VpnServerHealth.Degraded;

        var now = _timeProvider.GetUtcNow();

        server.Health = health;
        server.LastHealthCheckAt = now;
        server.LastHealthError = health == VpnServerHealth.Healthy ? null : "Xray is not running.";

        // A successful probe is what promotes a server out of Unverified — and what brings one
        // back after it was marked Unreachable by the sweep.
        if (server.Status is VpnServerStatus.Unverified or VpnServerStatus.Unreachable
            && health == VpnServerHealth.Healthy)
        {
            server.Status = VpnServerStatus.Active;
        }

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.ServerProbed, nameof(VpnServer), server.Id) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("health", health)
                    .Set("xrayRunning", status.Value.XrayRunning)
                    .Set("inbounds", inboundCount),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult<ServerProbeResult>.Success(new ServerProbeResult(
            true, health, server.LastHealthError, status.Value.XrayRunning,
            status.Value.XrayVersion, inboundCount));
    }

    public async Task<OperationResult<IReadOnlyList<DiscoveredInbound>>> DiscoverInboundsAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await _db.VpnServers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serverId, cancellationToken);

        if (server is null)
        {
            return OperationResult<IReadOnlyList<DiscoveredInbound>>.Failure(VpnServerErrors.NotFound);
        }

        var endpoint = EndpointFor(server);

        if (endpoint is null)
        {
            return OperationResult<IReadOnlyList<DiscoveredInbound>>.Failure(VpnServerErrors.TokenUnreadable);
        }

        var inbounds = await _panel.ListInboundsAsync(endpoint, cancellationToken);

        if (!inbounds.IsSuccess)
        {
            return OperationResult<IReadOnlyList<DiscoveredInbound>>.Failure(
                MapOutcome(inbounds.Outcome));
        }

        var allowlisted = await _db.ServerInboundProfiles
            .AsNoTracking()
            .Where(p => p.ServerId == serverId)
            .Select(p => p.InboundId)
            .ToListAsync(cancellationToken);

        var known = allowlisted.ToHashSet();

        var discovered = inbounds.Value!
            .Select(inbound => new DiscoveredInbound(
                inbound.Id,
                inbound.Remark,
                inbound.Protocol,
                inbound.Enable,
                inbound.Port,
                known.Contains(inbound.Id)))
            .OrderBy(inbound => inbound.InboundId)
            .ToList();

        return OperationResult<IReadOnlyList<DiscoveredInbound>>.Success(discovered);
    }

    public async Task<OperationResult> AllowlistInboundAsync(
        Guid serverId,
        int inboundId,
        string label,
        CancellationToken cancellationToken = default)
    {
        if (inboundId <= 0)
        {
            return OperationResult.Failure(VpnServerErrors.InboundNotOnPanel);
        }

        var server = await _db.VpnServers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serverId, cancellationToken);

        if (server is null)
        {
            return OperationResult.Failure(VpnServerErrors.NotFound);
        }

        var endpoint = EndpointFor(server);

        if (endpoint is null)
        {
            return OperationResult.Failure(VpnServerErrors.TokenUnreadable);
        }

        // Confirmed against the panel rather than trusted from the form. An id that does not
        // exist there would produce a profile the portal happily selects and then fails to
        // provision against, which is a much more confusing failure than this one.
        var inbounds = await _panel.ListInboundsAsync(endpoint, cancellationToken);

        if (!inbounds.IsSuccess)
        {
            return OperationResult.Failure(MapOutcome(inbounds.Outcome));
        }

        var match = inbounds.Value!.FirstOrDefault(inbound => inbound.Id == inboundId);

        if (match is null)
        {
            return OperationResult.Failure(VpnServerErrors.InboundNotOnPanel);
        }

        if (await _db.ServerInboundProfiles
                .AnyAsync(p => p.ServerId == serverId && p.InboundId == inboundId, cancellationToken))
        {
            // Already allowlisted; the operator got what they wanted.
            return OperationResult.Success();
        }

        var now = _timeProvider.GetUtcNow();

        _db.ServerInboundProfiles.Add(new ServerInboundProfile
        {
            Id = SequentialGuid.New(now),
            ServerId = serverId,
            InboundId = inboundId,
            Label = string.IsNullOrWhiteSpace(label) ? $"{match.Protocol}:{match.Port}" : label.Trim(),
            Protocol = match.Protocol,
            Remark = match.Remark,
            IsEnabled = true,
            LastSeenAt = now,
        });

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.InboundAllowlisted, nameof(ServerInboundProfile), serverId) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("serverSlug", server.Key)
                    .Set("inboundId", inboundId)
                    .Set("protocol", match.Protocol),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> SetInboundEnabledAsync(
        Guid profileId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        var profile = await _db.ServerInboundProfiles
            .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);

        if (profile is null)
        {
            return OperationResult.Failure(VpnServerErrors.NotFound);
        }

        // Disabling leaves existing clients attached, which is what makes draining an inbound
        // possible without cutting anybody off.
        profile.IsEnabled = isEnabled;

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.InboundToggled, nameof(ServerInboundProfile), profileId) with
            {
                Metadata = AuditMetadata.Create()
                    .Set("inboundId", profile.InboundId)
                    .Set("enabled", isEnabled),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveInboundAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await _db.ServerInboundProfiles
            .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);

        if (profile is null)
        {
            return OperationResult.Failure(VpnServerErrors.NotFound);
        }

        _db.ServerInboundProfiles.Remove(profile);

        await _audit.RecordAsync(
            AuditEntry.For(VpnAuditActions.InboundRemoved, nameof(ServerInboundProfile), profileId) with
            {
                Metadata = AuditMetadata.Create().Set("inboundId", profile.InboundId),
            },
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult.Success();
    }

    public async Task<PanelEndpoint?> ResolveEndpointAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await _db.VpnServers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serverId, cancellationToken);

        return server is null ? null : EndpointFor(server);
    }

    // ------------------------------------------------------------------------- helpers ----

    private PanelEndpoint? EndpointFor(VpnServer server)
    {
        var token = _protector.Unprotect(server.EncryptedApiToken);

        return string.IsNullOrEmpty(token) ? null : new PanelEndpoint(server.BaseUrl, token);
    }

    private async Task RecordUnreachableAsync(
        VpnServer server,
        string error,
        CancellationToken cancellationToken)
    {
        server.Health = VpnServerHealth.Unreachable;
        server.LastHealthCheckAt = _timeProvider.GetUtcNow();
        server.LastHealthError = error.Length <= 500 ? error : error[..500];

        // An Active server that cannot be reached is moved out of selection, but a Disabled or
        // Draining one keeps the state an operator chose — the sweep reports, it does not overrule.
        if (server.Status == VpnServerStatus.Active)
        {
            server.Status = VpnServerStatus.Unreachable;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string Describe(PanelOutcome outcome, string? message) => outcome switch
    {
        PanelOutcome.Unauthorized => "The panel refused the API token.",
        PanelOutcome.Blocked => "The panel address is not allowed.",
        PanelOutcome.NotFound => "The panel did not recognise the API path. Check the base path.",
        PanelOutcome.UnknownOutcome => "The panel did not answer.",
        _ => message ?? "The panel refused the request.",
    };

    private static string MapOutcome(PanelOutcome outcome) => outcome switch
    {
        PanelOutcome.Unauthorized => VpnServerErrors.TokenUnreadable,
        PanelOutcome.Blocked => VpnServerErrors.BaseUrlInvalid,
        _ => VpnServerErrors.NotFound,
    };

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "invalid";
}

/// <summary>
/// Audit actions for the VPN module.
/// <para>
/// Declared here rather than in the shared <c>AuditActions</c> so the module's vocabulary lives
/// with the module — the same reason its entities do.
/// </para>
/// </summary>
public static class VpnAuditActions
{
    public const string ServerCreated = "vpn.server.created";
    public const string ServerUpdated = "vpn.server.updated";
    public const string ServerProbed = "vpn.server.probed";
    public const string InboundAllowlisted = "vpn.inbound.allowlisted";
    public const string InboundToggled = "vpn.inbound.toggled";
    public const string InboundRemoved = "vpn.inbound.removed";

    public const string PlanCreated = "vpn.plan.created";
    public const string PlanUpdated = "vpn.plan.updated";
    public const string PlanDeleted = "vpn.plan.deleted";
    public const string PlanRuleAdded = "vpn.plan.rule.added";
    public const string PlanRuleRemoved = "vpn.plan.rule.removed";

    public const string ServiceCreated = "vpn.service.created";
    public const string ServiceProvisioned = "vpn.service.provisioned";
    public const string ServiceSuspended = "vpn.service.suspended";
    public const string ServiceResumed = "vpn.service.resumed";
    public const string ServiceRenewed = "vpn.service.renewed";
    public const string ServiceTrafficReset = "vpn.service.traffic.reset";
    public const string ServiceDecommissioned = "vpn.service.decommissioned";
    public const string ServiceLinkRotated = "vpn.service.link.rotated";
    public const string ServiceNeedsAttention = "vpn.service.needsAttention";
    public const string ServiceReconciled = "vpn.service.reconciled";
}
