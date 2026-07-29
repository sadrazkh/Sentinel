using System.Net;
using Sentinel.Application.Subscriptions;

namespace Sentinel.UnitTests.Subscriptions;

/// <summary>
/// The SSRF guard. Fetching a subscription makes the server issue a request to a URL somebody
/// else supplied, which turns the portal into a proxy for everything it can reach.
/// </summary>
public sealed class IpAddressPolicyTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    public void A_public_address_is_allowed(string address)
    {
        Assert.True(IpAddressPolicy.IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("127.0.0.1", IpRejection.Loopback)]
    [InlineData("127.1.2.3", IpRejection.Loopback)]
    [InlineData("::1", IpRejection.Loopback)]
    public void Loopback_is_refused(string address, IpRejection expected)
    {
        Assert.Equal(expected, IpAddressPolicy.Evaluate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    public void A_private_range_is_refused(string address)
    {
        Assert.Equal(IpRejection.Private, IpAddressPolicy.Evaluate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("11.0.0.1")]
    [InlineData("192.167.1.1")]
    public void An_address_just_outside_a_private_range_is_allowed(string address)
    {
        // Boundary check: 172.16/12 is 172.16–172.31, not all of 172.
        Assert.True(IpAddressPolicy.IsAllowed(IPAddress.Parse(address)));
    }

    [Fact]
    public void The_cloud_metadata_address_is_refused()
    {
        // The single most valuable SSRF target: on most providers it hands out instance
        // credentials to anyone who asks.
        Assert.Equal(
            IpRejection.LinkLocal,
            IpAddressPolicy.Evaluate(IPAddress.Parse("169.254.169.254")));
    }

    [Theory]
    [InlineData("169.254.0.1")]
    [InlineData("fe80::1")]
    public void Link_local_is_refused(string address)
    {
        Assert.Equal(IpRejection.LinkLocal, IpAddressPolicy.Evaluate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("::")]
    public void The_unspecified_address_is_refused(string address)
    {
        // 0.0.0.0 routes to localhost on several network stacks.
        Assert.Equal(IpRejection.Unspecified, IpAddressPolicy.Evaluate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    public void Carrier_grade_nat_is_refused(string address)
    {
        Assert.Equal(IpRejection.CarrierGradeNat, IpAddressPolicy.Evaluate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("224.0.0.1", IpRejection.Multicast)]
    [InlineData("239.255.255.255", IpRejection.Multicast)]
    [InlineData("240.0.0.1", IpRejection.Reserved)]
    [InlineData("255.255.255.255", IpRejection.Reserved)]
    [InlineData("198.18.0.1", IpRejection.Reserved)]
    [InlineData("192.0.0.1", IpRejection.Reserved)]
    public void Multicast_and_reserved_ranges_are_refused(string address, IpRejection expected)
    {
        Assert.Equal(expected, IpAddressPolicy.Evaluate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    public void Unique_local_ipv6_is_refused(string address)
    {
        Assert.Equal(IpRejection.UniqueLocalIpv6, IpAddressPolicy.Evaluate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("::ffff:127.0.0.1", IpRejection.Loopback)]
    [InlineData("::ffff:10.0.0.1", IpRejection.Private)]
    [InlineData("::ffff:169.254.169.254", IpRejection.LinkLocal)]
    public void An_ipv4_address_wrapped_in_ipv6_is_still_that_address(
        string address,
        IpRejection expected)
    {
        // ::ffff:169.254.169.254 is the metadata endpoint wearing a disguise. Unwrapping
        // first is what stops it slipping past the IPv4 rules as "some IPv6 address".
        Assert.Equal(expected, IpAddressPolicy.Evaluate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("2002:7f00:0001::")]
    [InlineData("64:ff9b::7f00:1")]
    public void A_tunnelled_ipv4_address_is_refused(string address)
    {
        // 6to4 and NAT64 both wrap an IPv4 address that could be internal.
        Assert.Equal(IpRejection.TunnelledIpv4, IpAddressPolicy.Evaluate(IPAddress.Parse(address)));
    }

    [Fact]
    public void Every_rejection_reason_is_reachable()
    {
        // Guards against a reason being added to the enum and never actually applied.
        var reached = new[]
        {
            "127.0.0.1", "10.0.0.1", "169.254.1.1", "fc00::1",
            "224.0.0.1", "0.0.0.0", "100.64.0.1", "240.0.0.1", "2002::1",
        }.Select(a => IpAddressPolicy.Evaluate(IPAddress.Parse(a))).ToHashSet();

        Assert.Equal(9, reached.Count);
        Assert.DoesNotContain(IpRejection.None, reached);
    }
}
