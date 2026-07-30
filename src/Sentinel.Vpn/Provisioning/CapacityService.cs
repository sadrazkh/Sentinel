using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sentinel.Vpn.Domain;
using Sentinel.Vpn.Persistence;

namespace Sentinel.Vpn.Provisioning;

public enum ReservationOutcome
{
    Reserved = 0,

    /// <summary>The server filled up between selection and reservation.</summary>
    NoCapacity = 1,

    ServerNotFound = 2,

    /// <summary>
    /// Too many simultaneous writers to the same row. The caller should re-select rather than
    /// retrying the same server, which is probably the one under contention.
    /// </summary>
    Contended = 3,
}

public sealed record Reservation(ReservationOutcome Outcome, Guid ServerId)
{
    public bool IsSuccess => Outcome == ReservationOutcome.Reserved;
}

/// <summary>
/// Counts services against a server's ceiling.
/// <para>
/// The reservation has to be atomic, because two members ordering at the same moment must not both
/// be given the last slot — one of them would fail at provisioning time, which is the worst moment
/// to discover it. The counter is guarded by the server row's optimistic concurrency token: the
/// loser of a race gets a <see cref="DbUpdateConcurrencyException"/> and re-reads, rather than
/// silently overwriting the winner's increment.
/// </para>
/// <para>
/// A raw <c>UPDATE … SET Reserved = Reserved + 1 WHERE Reserved &lt; Max</c> would also work and in
/// one statement, but it would be provider-specific SQL in a codebase that deliberately keeps the
/// engine swappable. The retry loop costs a few reads under contention and nothing at all otherwise.
/// </para>
/// </summary>
public interface ICapacityService
{
    Task<Reservation> ReserveAsync(Guid serverId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives a slot back.
    /// <para>
    /// Called when provisioning fails <em>certainly</em> — never on an unknown outcome, because a
    /// client that may exist on the panel is still occupying capacity.
    /// </para>
    /// </summary>
    Task ReleaseAsync(Guid serverId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recomputes a server's counter from the services actually placed on it.
    /// <para>
    /// The counter is a cache, and any cache drifts — a crash between the reservation and the
    /// service row, a database restored from a backup. This is what the reconciliation sweep calls
    /// to make it true again.
    /// </para>
    /// </summary>
    Task<int> RecountAsync(Guid serverId, CancellationToken cancellationToken = default);
}

public sealed class CapacityService : ICapacityService
{
    /// <summary>
    /// Attempts before giving up on a contended row. Three is generous: the write is a single
    /// integer, so a genuine collision resolves on the next read.
    /// </summary>
    private const int MaxAttempts = 3;

    private readonly IVpnDbContext _db;
    private readonly ILogger<CapacityService> _logger;

    public CapacityService(IVpnDbContext db, ILogger<CapacityService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Reservation> ReserveAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var server = await _db.VpnServers
                .FirstOrDefaultAsync(candidate => candidate.Id == serverId, cancellationToken);

            if (server is null)
            {
                return new Reservation(ReservationOutcome.ServerNotFound, serverId);
            }

            // Re-checked here and not only at selection: the gap between the two is exactly where a
            // concurrent order fits.
            if (server.ReservedClients >= server.MaxClients)
            {
                return new Reservation(ReservationOutcome.NoCapacity, serverId);
            }

            server.ReservedClients++;

            try
            {
                await _db.SaveChangesAsync(cancellationToken);

                return new Reservation(ReservationOutcome.Reserved, serverId);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Somebody else reserved on this server first. The tracked copy still carries the
                // original token, so it has to be re-read — retrying with it would resubmit the same
                // losing UPDATE for ever.
                await _db.ReloadAsync(server, cancellationToken);

                _logger.LogDebug(
                    "Capacity reservation on server {ServerId} lost a race (attempt {Attempt}).",
                    serverId,
                    attempt);
            }
        }

        _logger.LogWarning(
            "Could not reserve capacity on server {ServerId} after {Attempts} attempts.",
            serverId,
            MaxAttempts);

        return new Reservation(ReservationOutcome.Contended, serverId);
    }

    public async Task ReleaseAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var server = await _db.VpnServers
                .FirstOrDefaultAsync(candidate => candidate.Id == serverId, cancellationToken);

            if (server is null)
            {
                return;
            }

            // Floored at zero. A double release is a bug, but a negative counter would make the
            // server look like it had capacity it does not.
            server.ReservedClients = Math.Max(0, server.ReservedClients - 1);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                await _db.ReloadAsync(server, cancellationToken);
            }
        }

        // Not thrown: failing to release leaves a slot reserved, which the recount corrects. Failing
        // loudly here would turn a self-healing inaccuracy into a customer-visible error.
        _logger.LogWarning(
            "Could not release capacity on server {ServerId}; the recount will correct it.", serverId);
    }

    public async Task<int> RecountAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        // Counts every service not yet finished with the server. A service being provisioned occupies
        // a slot just as much as an active one — and a decommissioning one still has a client on the
        // panel until the job completes.
        var actual = await _db.CustomerServices
            .AsNoTracking()
            .CountAsync(
                service => service.ServerId == serverId
                           && service.Status != CustomerServiceStatus.Ended,
                cancellationToken);

        var server = await _db.VpnServers
            .FirstOrDefaultAsync(candidate => candidate.Id == serverId, cancellationToken);

        if (server is null || server.ReservedClients == actual)
        {
            return actual;
        }

        _logger.LogInformation(
            "Server {ServerId} capacity counter corrected from {Was} to {Now}.",
            serverId,
            server.ReservedClients,
            actual);

        server.ReservedClients = actual;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A reservation landed mid-recount, so the number just written is already out of date.
            // The next sweep settles it; forcing the write would undo a real reservation.
            _logger.LogDebug(
                "Recount for server {ServerId} raced a reservation; deferring to the next sweep.",
                serverId);
        }

        return actual;
    }
}
