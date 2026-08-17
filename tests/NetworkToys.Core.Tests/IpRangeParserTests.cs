using System.Net;
using NetworkToys.Core.Addressing;
using Xunit;

namespace NetworkToys.Core.Tests;

public class IpRangeParserTests
{
    private static IpRangeKind Expand(string token, out List<IPAddress> addresses, int limit = IpRangeParser.DefaultLimit)
        => IpRangeParser.TryExpand(token, limit, out addresses, out _);

    [Theory]
    [InlineData("example.jp")]
    [InlineData("192.168.1.1")]
    [InlineData("")]
    [InlineData("   ")]
    public void Plain_hosts_are_not_ranges(string token)
        => Assert.Equal(IpRangeKind.NotARange, Expand(token, out _));

    [Theory]
    [InlineData("my-server")]
    [InlineData("web-01.example.jp")]
    [InlineData("a-b-c")]
    public void Hostnames_with_hyphens_are_not_ranges(string token)
        => Assert.Equal(IpRangeKind.NotARange, Expand(token, out _));

    [Fact]
    public void Expands_a_short_range()
    {
        Assert.Equal(IpRangeKind.Expanded, Expand("192.168.1.5-8", out List<IPAddress> addresses));

        Assert.Equal(4, addresses.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.5"), addresses[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.8"), addresses[3]);
    }

    [Fact]
    public void A_range_of_one_is_allowed()
    {
        Assert.Equal(IpRangeKind.Expanded, Expand("10.0.0.5-5", out List<IPAddress> addresses));
        Assert.Single(addresses);
    }

    [Fact]
    public void Expands_across_octet_boundaries()
    {
        Assert.Equal(IpRangeKind.Expanded, Expand("10.0.0.254-10.0.1.1", out List<IPAddress> addresses));

        Assert.Equal(4, addresses.Count);
        Assert.Equal(IPAddress.Parse("10.0.1.0"), addresses[2]);
    }

    [Fact]
    public void Cidr_24_drops_network_and_broadcast()
    {
        Assert.Equal(IpRangeKind.Expanded, Expand("192.168.1.0/24", out List<IPAddress> addresses));

        Assert.Equal(254, addresses.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), addresses[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.254"), addresses[253]);
    }

    [Fact]
    public void Cidr_31_keeps_both_addresses()
    {
        // 点対点リンクは 2 アドレスとも使う
        Assert.Equal(IpRangeKind.Expanded, Expand("10.0.0.0/31", out List<IPAddress> addresses));
        Assert.Equal(2, addresses.Count);
    }

    [Fact]
    public void Cidr_32_is_a_single_host()
    {
        Assert.Equal(IpRangeKind.Expanded, Expand("10.0.0.7/32", out List<IPAddress> addresses));

        Assert.Single(addresses);
        Assert.Equal(IPAddress.Parse("10.0.0.7"), addresses[0]);
    }

    [Fact]
    public void Cidr_normalizes_a_host_address_to_its_network()
    {
        Assert.Equal(IpRangeKind.Expanded, Expand("192.168.1.130/29", out List<IPAddress> addresses));

        Assert.Equal(6, addresses.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.129"), addresses[0]);
    }

    [Theory]
    [InlineData("192.168.1.0/33")]
    [InlineData("192.168.1.0/abc")]
    public void Bad_prefixes_are_invalid(string token)
        => Assert.Equal(IpRangeKind.Invalid, Expand(token, out _));

    [Fact]
    public void Reversed_ranges_are_invalid()
        => Assert.Equal(IpRangeKind.Invalid, Expand("192.168.1.100-10", out _));

    [Fact]
    public void Unreadable_range_ends_are_invalid()
        => Assert.Equal(IpRangeKind.Invalid, Expand("192.168.1.1-xyz", out _));

    [Fact]
    public void Ranges_beyond_the_limit_are_invalid()
    {
        Assert.Equal(IpRangeKind.Invalid, Expand("10.0.0.0/16", out _, limit: 4096));
        Assert.Equal(IpRangeKind.Expanded, Expand("10.0.0.0/16", out _, limit: 70000));
    }

    [Theory]
    [InlineData("1", false)]              // TryParse なら 0.0.0.1 になってしまうが、ここでは弾く
    [InlineData("192.168.1", false)]
    [InlineData("192.168.1.256", false)]
    [InlineData("192.168.01.1", true)]    // 前ゼロは許容する
    [InlineData("192.168.1.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("192.168.1.a", false)]
    [InlineData("192.168.1.1.1", false)]
    [InlineData("192.168..1", false)]
    public void TryParseIPv4_only_accepts_dotted_quads(string text, bool expected)
        => Assert.Equal(expected, IpRangeParser.TryParseIPv4(text, out _));

    [Theory]
    [InlineData("999.999.999.999/24")]
    [InlineData("10.0.0/24")]
    [InlineData("192.168.1.0/24/8")]
    public void Broken_cidr_notation_is_invalid_not_a_hostname(string token)
        => Assert.Equal(IpRangeKind.Invalid, Expand(token, out _));
}
