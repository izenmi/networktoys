using System.Net;
using NetworkToys.Core.Addressing;
using Xunit;

namespace NetworkToys.Core.Tests;

public class IpMathTests
{
    [Theory]
    [InlineData("0.0.0.0", 0u)]
    [InlineData("127.0.0.1", 2130706433u)]
    [InlineData("192.168.1.1", 3232235777u)]
    [InlineData("255.255.255.255", 4294967295u)]
    public void ToUInt32_converts_known_addresses(string text, uint expected)
        => Assert.Equal(expected, IpMath.ToUInt32(IPAddress.Parse(text)));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.20.30.40")]
    [InlineData("192.168.1.1")]
    [InlineData("255.255.255.255")]
    public void FromUInt32_roundtrips(string text)
    {
        var address = IPAddress.Parse(text);
        Assert.Equal(address, IpMath.FromUInt32(IpMath.ToUInt32(address)));
    }

    [Fact]
    public void ToUInt32_rejects_ipv6()
        => Assert.Throws<ArgumentException>(() => IpMath.ToUInt32(IPAddress.Parse("::1")));

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(8, 0xFF000000u)]
    [InlineData(24, 0xFFFFFF00u)]
    [InlineData(31, 0xFFFFFFFEu)]
    [InlineData(32, 0xFFFFFFFFu)]
    public void PrefixToMask_covers_the_edges(int prefix, uint expected)
        => Assert.Equal(expected, IpMath.PrefixToMask(prefix));

    [Theory]
    [InlineData(-1)]
    [InlineData(33)]
    public void PrefixToMask_rejects_out_of_range(int prefix)
        => Assert.Throws<ArgumentOutOfRangeException>(() => IpMath.PrefixToMask(prefix));

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.0.0", 16)]
    [InlineData("0.0.0.0", 0)]
    [InlineData("255.255.255.255", 32)]
    public void MaskToPrefix_converts_back(string mask, int expected)
        => Assert.Equal(expected, IpMath.MaskToPrefix(IPAddress.Parse(mask)));

    [Fact]
    public void MaskToPrefix_rejects_non_contiguous_mask()
        => Assert.Throws<ArgumentException>(() => IpMath.MaskToPrefix(IPAddress.Parse("255.0.255.0")));

    [Theory]
    [InlineData("192.168.1.130", 24, "192.168.1.0", "192.168.1.255")]
    [InlineData("10.1.2.3", 16, "10.1.0.0", "10.1.255.255")]
    [InlineData("172.16.5.9", 30, "172.16.5.8", "172.16.5.11")]
    public void Network_and_broadcast_bound_the_range(string address, int prefix, string network, string broadcast)
    {
        var ip = IPAddress.Parse(address);
        Assert.Equal(IPAddress.Parse(network), IpMath.NetworkAddress(ip, prefix));
        Assert.Equal(IPAddress.Parse(broadcast), IpMath.BroadcastAddress(ip, prefix));
    }

    [Theory]
    [InlineData(24, 254)]
    [InlineData(16, 65534)]
    [InlineData(30, 2)]
    [InlineData(31, 2)]   // /31 は点対点。2 アドレスとも使う
    [InlineData(32, 1)]   // /32 は単一ホスト
    public void UsableHostCount_matches_field_expectations(int prefix, long expected)
        => Assert.Equal(expected, IpMath.UsableHostCount(prefix));

    [Theory]
    [InlineData("192.168.1.10", "192.168.1.200", 24, true)]
    [InlineData("192.168.1.10", "192.168.2.10", 24, false)]
    [InlineData("192.168.1.10", "192.168.2.10", 16, true)]
    public void IsSameSubnet_compares_masked_addresses(string a, string b, int prefix, bool expected)
        => Assert.Equal(expected, IpMath.IsSameSubnet(IPAddress.Parse(a), IPAddress.Parse(b), prefix));
}
