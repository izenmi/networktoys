using System.Net;
using NetworkToys.Core.Addressing;
using Xunit;

namespace NetworkToys.Core.Tests;

public class SubnetCalculatorTests
{
    private static SubnetInfo ParseOk(string text)
    {
        SubnetInfo? info = SubnetCalculator.Parse(text, out string? error);
        Assert.Null(error);
        Assert.NotNull(info);
        return info;
    }

    [Fact]
    public void A_slash24_is_fully_described()
    {
        SubnetInfo info = ParseOk("192.168.1.130/24");

        Assert.Equal(IPAddress.Parse("192.168.1.0"), info.Network);
        Assert.Equal(IPAddress.Parse("192.168.1.255"), info.Broadcast);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), info.Mask);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), info.FirstHost);
        Assert.Equal(IPAddress.Parse("192.168.1.254"), info.LastHost);
        Assert.Equal(254, info.UsableHosts);
        Assert.Equal("192.168.1.0/24", info.Cidr);
    }

    [Fact]
    public void An_ip_with_a_dotted_mask_is_accepted()
    {
        SubnetInfo info = ParseOk("10.0.5.20 255.255.252.0");

        Assert.Equal(22, info.PrefixLength);
        Assert.Equal(IPAddress.Parse("10.0.4.0"), info.Network);
        Assert.Equal(1022, info.UsableHosts);
    }

    [Fact]
    public void A_point_to_point_31_keeps_both_hosts()
    {
        SubnetInfo info = ParseOk("10.0.0.0/31");

        Assert.Equal(IPAddress.Parse("10.0.0.0"), info.FirstHost);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), info.LastHost);
        Assert.Equal(2, info.UsableHosts);
    }

    [Fact]
    public void A_host_route_32_is_a_single_address()
    {
        SubnetInfo info = ParseOk("10.0.0.7/32");

        Assert.Equal(info.Network, info.FirstHost);
        Assert.Equal(info.Network, info.LastHost);
        Assert.Equal(1, info.UsableHosts);
    }

    [Fact]
    public void The_whole_v4_space_does_not_overflow()
    {
        SubnetInfo info = ParseOk("0.0.0.0/0");

        Assert.Equal(IPAddress.Parse("255.255.255.255"), info.Broadcast);
        Assert.Equal(4294967294, info.UsableHosts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_is_not_an_error(string text)
    {
        Assert.Null(SubnetCalculator.Parse(text, out string? error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("192.168.1.0/33")]
    [InlineData("999.1.1.1/24")]
    [InlineData("192.168.1.0 255.255.0.255")]
    [InlineData("example.jp")]
    public void Broken_input_reports_a_reason(string text)
    {
        Assert.Null(SubnetCalculator.Parse(text, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Splitting_a_24_into_26_yields_four_subnets()
    {
        SubnetInfo parent = ParseOk("192.168.1.0/24");

        IReadOnlyList<SubnetInfo> children = SubnetCalculator.Split(parent, 26);

        Assert.Equal(4, children.Count);
        Assert.Equal("192.168.1.0/26", children[0].Cidr);
        Assert.Equal("192.168.1.192/26", children[3].Cidr);
        Assert.Equal(62, children[0].UsableHosts);
    }

    [Fact]
    public void Splitting_respects_the_display_limit()
    {
        SubnetInfo parent = ParseOk("10.0.0.0/16");

        IReadOnlyList<SubnetInfo> children = SubnetCalculator.Split(parent, 24, limit: 16);

        Assert.Equal(16, children.Count);
        Assert.Equal(256, SubnetCalculator.SplitCount(16, 24));
    }

    [Fact]
    public void Splitting_to_32_enumerates_host_routes()
    {
        SubnetInfo parent = ParseOk("10.0.0.0/30");

        IReadOnlyList<SubnetInfo> children = SubnetCalculator.Split(parent, 32);

        Assert.Equal(4, children.Count);
        Assert.Equal("10.0.0.3/32", children[3].Cidr);
    }
}
