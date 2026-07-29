using System.Net;
using System.Net.Sockets;

namespace Sentinel.Application.Subscriptions;

public enum IpRejection
{
    None = 0,
    Loopback = 1,
    Private = 2,
    LinkLocal = 3,
    UniqueLocalIpv6 = 4,
    Multicast = 5,
    Unspecified = 6,
    CarrierGradeNat = 7,
    Reserved = 8,
    /// <summary>A 6to4 or NAT64 address, which carries an IPv4 address that could be internal.</summary>
    TunnelledIpv4 = 9,
    UnsupportedFamily = 10,
}

/// <summary>
/// Decides whether the portal may open a connection to an address.
/// <para>
/// This is the core of the SSRF defence. Fetching a subscription means the server makes a
/// request to a URL somebody else supplied, which turns the portal into a proxy for anything it
/// can reach — the database on the private network, another container, and above all the cloud
/// metadata endpoint at 169.254.169.254, which on most providers hands out instance
/// credentials to anyone who asks.
/// </para>
/// <para>
/// A pure function on an <see cref="IPAddress"/>, deliberately: the check has to happen against
/// the address actually being connected to, not against a hostname that could resolve
/// differently a moment later.
/// </para>
/// </summary>
public static class IpAddressPolicy
{
    public static bool IsAllowed(IPAddress address) => Evaluate(address) == IpRejection.None;

    public static IpRejection Evaluate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // An IPv4 address written as ::ffff:10.0.0.1 is still 10.0.0.1. Unwrapping first means
        // the IPv4 rules below apply to it, instead of it slipping past them as "some IPv6".
        if (address.IsIPv4MappedToIPv6)
        {
            return EvaluateIpv4(address.MapToIPv4());
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => EvaluateIpv4(address),
            AddressFamily.InterNetworkV6 => EvaluateIpv6(address),

            // Anything else is not something an HTTP fetch should be reaching.
            _ => IpRejection.UnsupportedFamily,
        };
    }

    private static IpRejection EvaluateIpv4(IPAddress address)
    {
        var octets = address.GetAddressBytes();

        return octets switch
        {
            // 0.0.0.0/8 — "this network". 0.0.0.0 routes to localhost on several stacks.
            [0, ..] => IpRejection.Unspecified,

            // 127.0.0.0/8
            [127, ..] => IpRejection.Loopback,

            // 10.0.0.0/8
            [10, ..] => IpRejection.Private,

            // 172.16.0.0/12
            [172, >= 16 and <= 31, ..] => IpRejection.Private,

            // 192.168.0.0/16
            [192, 168, ..] => IpRejection.Private,

            // 169.254.0.0/16 — link-local, and the range the cloud metadata service lives in.
            [169, 254, ..] => IpRejection.LinkLocal,

            // 100.64.0.0/10 — carrier-grade NAT, routable inside a provider's network.
            [100, >= 64 and <= 127, ..] => IpRejection.CarrierGradeNat,

            // 192.0.0.0/24 and 192.0.2.0/24 — IETF protocol assignments and TEST-NET-1.
            [192, 0, 0 or 2, _] => IpRejection.Reserved,

            // 198.18.0.0/15 — benchmarking.
            [198, 18 or 19, ..] => IpRejection.Reserved,

            // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, 255.255.255.255 broadcast.
            [>= 224 and <= 239, ..] => IpRejection.Multicast,
            [>= 240, ..] => IpRejection.Reserved,

            _ => IpRejection.None,
        };
    }

    private static IpRejection EvaluateIpv6(IPAddress address)
    {
        if (IPAddress.IPv6Loopback.Equals(address))
        {
            return IpRejection.Loopback;
        }

        if (IPAddress.IPv6Any.Equals(address))
        {
            return IpRejection.Unspecified;
        }

        if (address.IsIPv6LinkLocal)
        {
            return IpRejection.LinkLocal;
        }

        if (address.IsIPv6Multicast)
        {
            return IpRejection.Multicast;
        }

        var bytes = address.GetAddressBytes();

        // fc00::/7 — unique local, the IPv6 equivalent of a private range.
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return IpRejection.UniqueLocalIpv6;
        }

        // 2002::/16 (6to4) and 64:ff9b::/96 (NAT64) both wrap an IPv4 address. Rather than
        // extract and re-check it, they are refused outright: neither is something a
        // subscription host legitimately needs, and both are known SSRF bypass routes.
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            return IpRejection.TunnelledIpv4;
        }

        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B)
        {
            return IpRejection.TunnelledIpv4;
        }

        // ::/96 IPv4-compatible — deprecated, and another way to wrap an internal address.
        if (bytes.Take(12).All(b => b == 0))
        {
            return IpRejection.TunnelledIpv4;
        }

        return IpRejection.None;
    }
}
