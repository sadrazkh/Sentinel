using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Sentinel.Application.Subscriptions;

namespace Sentinel.Application.Security;

/// <summary>
/// Raised when a target resolves to an address the portal refuses to reach.
/// <para>
/// A distinct type so a caller can report "blocked" rather than a generic network failure — the
/// two mean different things: blocked is certain and safe, a network failure is neither.
/// </para>
/// </summary>
public sealed class BlockedAddressException : Exception
{
    public BlockedAddressException(string message) : base(message)
    {
    }
}

/// <summary>
/// The connect-time half of the SSRF defence, shared by every outbound HTTP client the portal has.
/// <para>
/// Screening a URL before the request is the obvious layer and the weaker one: between the check
/// and the connection, DNS can answer differently — the rebinding attack. This resolves the host,
/// refuses the attempt if <em>any</em> returned address is one we will not reach, and then connects
/// to an address that was actually validated. TLS still uses the original host name for SNI and
/// certificate validation, so pinning the address costs nothing in correctness.
/// </para>
/// <para>
/// One implementation rather than one per client: this is the control that actually holds, and a
/// second copy would be the one that drifts.
/// </para>
/// </summary>
public static class ValidatedAddressConnector
{
    /// <summary>
    /// Builds a connect callback for <see cref="SocketsHttpHandler.ConnectCallback"/>.
    /// </summary>
    /// <param name="allowLoopback">
    /// Whether loopback is permitted. Off in production for every caller. The integration suite
    /// turns it on because its fake upstream necessarily runs on localhost — and it is a
    /// constructor argument rather than an ambient setting so that enabling it is visible at the
    /// call site rather than hidden in configuration.
    /// </param>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Create(
        bool allowLoopback = false) =>
        (context, cancellationToken) =>
            ConnectAsync(context.DnsEndPoint, allowLoopback, cancellationToken);

    private static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endPoint,
        bool allowLoopback,
        CancellationToken cancellationToken)
    {
        var resolved = IPAddress.TryParse(endPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken);

        if (resolved.Length == 0)
        {
            throw new BlockedAddressException("The target did not resolve to any address.");
        }

        // Every returned address must pass. Connecting to the one good address in a set that also
        // contains an internal one would still be a successful rebinding attack, because the
        // attacker controls which address the resolver returns next.
        foreach (var address in resolved)
        {
            if (allowLoopback && IPAddress.IsLoopback(address))
            {
                continue;
            }

            var rejection = IpAddressPolicy.Evaluate(address);

            if (rejection != IpRejection.None)
            {
                throw new BlockedAddressException(
                    $"The target resolves to a disallowed address ({rejection}).");
            }
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            // Connect to the validated addresses, never to the host name again.
            await socket.ConnectAsync(resolved, endPoint.Port, cancellationToken);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
