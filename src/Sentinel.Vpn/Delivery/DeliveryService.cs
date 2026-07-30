using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Panel;
using Sentinel.Vpn.Persistence;
using Sentinel.Vpn.Servers;

namespace Sentinel.Vpn.Delivery;

/// <summary>How the caller wants the configurations.</summary>
public enum DeliveryFormat
{
    /// <summary>
    /// One base64 blob of newline-separated URIs — what a VPN client application expects from a
    /// subscription URL, and what it polls for on its own schedule.
    /// </summary>
    Subscription = 0,

    /// <summary>Plain newline-separated URIs, for a person reading or copying them.</summary>
    Plain = 1,
}

public enum DeliveryOutcome
{
    Delivered = 0,

    /// <summary>No such token, malformed, or revoked. All three answer identically.</summary>
    NotFound = 1,

    /// <summary>The token is real but the service is expired, exhausted or suspended.</summary>
    NotUsable = 2,

    /// <summary>The panel could not be reached, so there is nothing to serve right now.</summary>
    Unavailable = 3,
}

public sealed record DeliveryResult(DeliveryOutcome Outcome, string? Body, int ConfigCount)
{
    public bool IsSuccess => Outcome == DeliveryOutcome.Delivered;

    public static DeliveryResult Failure(DeliveryOutcome outcome) => new(outcome, null, 0);
}

/// <summary>
/// Serves a member's configurations from an unauthenticated URL.
/// <para>
/// The token is the whole authorisation, because a VPN client application cannot sign in. Everything
/// here follows from that:
/// </para>
/// <list type="bullet">
/// <item>The lookup is by <b>hash</b>, so a database leak yields nothing usable.</item>
/// <item>Not-found, malformed and revoked are one answer, so the endpoint cannot be used to learn
/// which tokens exist.</item>
/// <item>Usability is re-checked on every request against the clock and the quota — the status
/// recorded by the last sweep is not trusted on its own.</item>
/// <item>The configuration URIs are fetched from the panel each time rather than stored. They carry
/// the member's own proxy credentials, and the portal has no reason to hold a second copy.</item>
/// </list>
/// </summary>
public interface IDeliveryService
{
    Task<DeliveryResult> DeliverAsync(
        string? token,
        DeliveryFormat format,
        CancellationToken cancellationToken = default);
}

public sealed class DeliveryService : IDeliveryService
{
    private readonly IVpnDbContext _db;
    private readonly IThreeXUiClient _panel;
    private readonly IVpnServerAdminService _servers;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeliveryService> _logger;

    public DeliveryService(
        IVpnDbContext db,
        IThreeXUiClient panel,
        IVpnServerAdminService servers,
        TimeProvider timeProvider,
        ILogger<DeliveryService> logger)
    {
        _db = db;
        _panel = panel;
        _servers = servers;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<DeliveryResult> DeliverAsync(
        string? token,
        DeliveryFormat format,
        CancellationToken cancellationToken = default)
    {
        // Shape first, so a crafted value never reaches a hash computation or a query.
        if (!DeliveryToken.IsWellFormed(token))
        {
            return DeliveryResult.Failure(DeliveryOutcome.NotFound);
        }

        var hash = DeliveryToken.Hash(token!);

        var service = await _db.CustomerServices
            .AsNoTracking()
            .Where(candidate => candidate.DeliveryTokenHash == hash)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Status,
                candidate.ServerId,
                candidate.PanelClientEmail,
                candidate.TrafficBytes,
                candidate.UsedBytes,
                candidate.ExpiresAt,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (service is null || service.ServerId is null || service.PanelClientEmail is null)
        {
            return DeliveryResult.Failure(DeliveryOutcome.NotFound);
        }

        var now = _timeProvider.GetUtcNow();

        // Re-evaluated here rather than trusting the stored status: a service can pass its expiry or
        // its quota between usage sweeps, and this endpoint is what a client polls in between.
        var usable = service.Status == CustomerServiceStatus.Active
                     && (service.ExpiresAt is not { } expires || expires > now)
                     && (service.TrafficBytes <= 0 || service.UsedBytes < service.TrafficBytes);

        if (!usable)
        {
            // Distinguished from not-found on purpose: the holder of a valid token already knows the
            // service exists, so telling them it has lapsed is useful rather than a disclosure.
            return DeliveryResult.Failure(DeliveryOutcome.NotUsable);
        }

        var endpoint = await _servers.ResolveEndpointAsync(service.ServerId.Value, cancellationToken);

        if (endpoint is null)
        {
            return DeliveryResult.Failure(DeliveryOutcome.Unavailable);
        }

        // Read from the panel rather than assembled here. Building a vless:// URI ourselves would mean
        // reimplementing the panel's own logic and drifting from it on every upgrade — and the panel
        // already returns exactly the strings its own copy button produces.
        var links = await _panel.GetClientLinksAsync(
            endpoint, service.PanelClientEmail, cancellationToken);

        if (!links.IsSuccess)
        {
            _logger.LogWarning(
                "Delivery for link {Fingerprint} could not read the panel: {Outcome}.",
                DeliveryToken.Fingerprint(token),
                links.Outcome);

            return DeliveryResult.Failure(DeliveryOutcome.Unavailable);
        }

        var uris = links.Value!
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .ToList();

        if (uris.Count == 0)
        {
            // The client exists but has no URL-shaped configuration. Reported as unavailable rather
            // than as an empty success, which a client application would cache as "nothing to use".
            return DeliveryResult.Failure(DeliveryOutcome.Unavailable);
        }

        var joined = string.Join('\n', uris);

        var body = format == DeliveryFormat.Subscription
            // Whole-body base64 is the de-facto subscription format: it is what the clients expect,
            // and it is what the portal's own subscription parser already reads.
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(joined))
            : joined;

        return new DeliveryResult(DeliveryOutcome.Delivered, body, uris.Count);
    }
}
