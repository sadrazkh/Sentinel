using Sentinel.Vpn.Domain;

namespace Sentinel.Vpn.Provisioning;

/// <summary>One candidate server, reduced to what selection needs.</summary>
public sealed record ServerCandidate(
    Guid ServerId,
    string Key,
    string CountryCode,
    VpnServerStatus Status,
    VpnServerHealth Health,
    int MaxClients,
    int ReservedClients,
    int SelectionPriority,
    int EnabledInboundCount)
{
    public int RemainingCapacity => Math.Max(0, MaxClients - ReservedClients);

    /// <summary>
    /// Load as a fraction. Guards the divide: a server with no ceiling reads as full rather than as
    /// infinitely free, which is the safe direction for a value that decides placement.
    /// </summary>
    public double LoadFactor => MaxClients <= 0 ? 1d : (double)ReservedClients / MaxClients;
}

public enum SelectionOutcome
{
    Selected = 0,

    /// <summary>No server exists in the requested country at all.</summary>
    NoServerInCountry = 1,

    /// <summary>Servers exist but none is active and reachable.</summary>
    NoHealthyServer = 2,

    /// <summary>Healthy servers exist but all are full.</summary>
    NoCapacity = 3,

    /// <summary>Servers have room but none has an inbound the portal may use.</summary>
    NoUsableInbound = 4,
}

public sealed record SelectionResult(SelectionOutcome Outcome, ServerCandidate? Server)
{
    public bool IsSuccess => Outcome == SelectionOutcome.Selected && Server is not null;

    public static SelectionResult Success(ServerCandidate server) => new(SelectionOutcome.Selected, server);

    public static SelectionResult Failure(SelectionOutcome outcome) => new(outcome, null);
}

/// <summary>
/// Chooses which panel a new service goes on.
/// <para>
/// A pure function, so every placement decision can be tested without a database or a panel. It is
/// also the only thing that decides placement: no request shape anywhere carries a server id, which
/// is what stops a member choosing their own server — and with it their own capacity, country and
/// inbound configuration.
/// </para>
/// <para>
/// The failure outcomes are distinguished rather than collapsed into "none available" because they
/// need different responses: no capacity means add a server, no usable inbound means finish
/// configuring one, and no healthy server means something is broken right now.
/// </para>
/// </summary>
public static class ServerSelector
{
    /// <summary>
    /// Picks a server for a plan.
    /// </summary>
    /// <param name="countryCode">
    /// The plan's country, or <c>null</c> for any. Comes from the plan row, never from a request.
    /// </param>
    public static SelectionResult Select(
        IReadOnlyList<ServerCandidate> candidates,
        string? countryCode)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Filtered in stages so the failure can say which stage everything fell out at. Diagnosing
        // "no server available" from a single predicate is guesswork.
        var inCountry = countryCode is null
            ? candidates
            : candidates
                .Where(server => string.Equals(
                    server.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (inCountry.Count == 0)
        {
            return SelectionResult.Failure(SelectionOutcome.NoServerInCountry);
        }

        // Draining is deliberately excluded: an operator set it precisely so no new service lands
        // there, while the ones already on it keep working.
        var healthy = inCountry
            .Where(server => server.Status == VpnServerStatus.Active
                             && server.Health != VpnServerHealth.Unreachable)
            .ToList();

        if (healthy.Count == 0)
        {
            return SelectionResult.Failure(SelectionOutcome.NoHealthyServer);
        }

        var withRoom = healthy.Where(server => server.RemainingCapacity > 0).ToList();

        if (withRoom.Count == 0)
        {
            return SelectionResult.Failure(SelectionOutcome.NoCapacity);
        }

        var usable = withRoom.Where(server => server.EnabledInboundCount > 0).ToList();

        if (usable.Count == 0)
        {
            return SelectionResult.Failure(SelectionOutcome.NoUsableInbound);
        }

        var chosen = usable
            // Priority is the operator's explicit preference and comes first.
            .OrderBy(server => server.SelectionPriority)
            // Then the emptiest, so services spread rather than piling onto whichever server was
            // created first.
            .ThenBy(server => server.LoadFactor)
            // Then the key, so the choice is deterministic. Without this, two equally-loaded servers
            // would be picked in whatever order the database happened to return, and a test would
            // pass or fail depending on it.
            .ThenBy(server => server.Key, StringComparer.Ordinal)
            .First();

        return SelectionResult.Success(chosen);
    }
}
