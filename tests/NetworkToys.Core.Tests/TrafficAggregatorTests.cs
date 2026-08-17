using System.Net;
using NetworkToys.Core.Net;
using Xunit;

namespace NetworkToys.Core.Tests;

public class TrafficAggregatorTests
{
    private static FlowKey Key(uint pid, ushort localPort)
        => FlowKey.ForTcp(false, pid,
            IPAddress.Parse("10.0.0.1").GetAddressBytes(), localPort,
            IPAddress.Parse("10.0.0.2").GetAddressBytes(), 80);

    [Fact]
    public void Bytes_accumulate_per_key_and_direction()
    {
        var aggregator = new TrafficAggregator();
        aggregator.Add(Key(100, 1000), sent: true, 100);
        aggregator.Add(Key(100, 1000), sent: true, 50);
        aggregator.Add(Key(100, 1000), sent: false, 30);
        aggregator.Add(Key(100, 2000), sent: true, 7);

        Dictionary<FlowKey, FlowTotals> drained = aggregator.Drain();

        Assert.Equal(2, drained.Count);
        Assert.Equal(new FlowTotals(150, 30), drained[Key(100, 1000)]);
        Assert.Equal(new FlowTotals(7, 0), drained[Key(100, 2000)]);
    }

    [Fact]
    public void Drain_resets_the_window()
    {
        var aggregator = new TrafficAggregator();
        aggregator.Add(Key(100, 1000), sent: true, 100);

        Assert.Single(aggregator.Drain());
        Assert.Empty(aggregator.Drain());
    }

    [Fact]
    public void Concurrent_adds_lose_nothing()
    {
        var aggregator = new TrafficAggregator();

        Parallel.For(0, 10_000, i => aggregator.Add(Key(100, (ushort)(1000 + i % 4)), sent: (i % 2) == 0, 1));

        long total = aggregator.Drain().Values.Sum(t => t.Sent + t.Received);
        Assert.Equal(10_000, total);
    }
}
