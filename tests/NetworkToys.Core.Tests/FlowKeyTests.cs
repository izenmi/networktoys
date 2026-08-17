using System.Net;
using NetworkToys.Core.Net;
using Xunit;

namespace NetworkToys.Core.Tests;

public class FlowKeyTests
{
    private static byte[] Bytes(string address) => IPAddress.Parse(address).GetAddressBytes();

    [Fact]
    public void Same_v4_flow_builds_the_same_key()
    {
        FlowKey a = FlowKey.ForTcp(false, 100, Bytes("10.0.0.1"), 1000, Bytes("10.0.0.2"), 80);
        FlowKey b = FlowKey.ForTcp(false, 100, Bytes("10.0.0.1"), 1000, Bytes("10.0.0.2"), 80);

        Assert.Equal(a, b);
        Assert.False(a.V6);
    }

    [Fact]
    public void Pid_is_part_of_the_key()
    {
        FlowKey a = FlowKey.ForTcp(false, 100, Bytes("127.0.0.1"), 1000, Bytes("127.0.0.1"), 2000);
        FlowKey b = FlowKey.ForTcp(false, 200, Bytes("127.0.0.1"), 1000, Bytes("127.0.0.1"), 2000);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void V4_mapped_v6_collapses_to_the_v4_key()
    {
        FlowKey mapped = FlowKey.ForTcp(true, 100, Bytes("::ffff:10.0.0.1"), 1000, Bytes("::ffff:10.0.0.2"), 80);
        FlowKey plain = FlowKey.ForTcp(false, 100, Bytes("10.0.0.1"), 1000, Bytes("10.0.0.2"), 80);

        Assert.Equal(plain, mapped);
    }

    [Fact]
    public void Real_v6_addresses_keep_all_bits()
    {
        FlowKey a = FlowKey.ForTcp(true, 100, Bytes("2001:db8::1"), 1000, Bytes("2001:db8::2"), 80);
        FlowKey b = FlowKey.ForTcp(true, 100, Bytes("2001:db8::1"), 1000, Bytes("2001:db8::3"), 80);

        Assert.True(a.V6);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Udp_folds_the_remote_side_to_zero()
    {
        FlowKey key = FlowKey.ForUdp(false, 100, Bytes("192.168.1.10"), 53);

        Assert.False(key.Tcp);
        Assert.Equal(0UL, key.BHi);
        Assert.Equal(0UL, key.BLo);
        Assert.Equal(0, key.BPort);
    }

    [Fact]
    public void Swapping_twice_returns_the_original()
    {
        FlowKey key = FlowKey.ForTcp(false, 100, Bytes("10.0.0.1"), 1000, Bytes("10.0.0.2"), 80);
        FlowKey swapped = key.Swapped();

        Assert.NotEqual(key, swapped);
        Assert.Equal(key, swapped.Swapped());
        Assert.Equal(key.APort, swapped.BPort);
    }
}
