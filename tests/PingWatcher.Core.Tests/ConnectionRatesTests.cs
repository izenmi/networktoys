using System.Net;
using PingWatcher.Core.Net;
using Xunit;

namespace PingWatcher.Core.Tests;

public class ConnectionRatesTests
{
    private static byte[] Bytes(string address) => IPAddress.Parse(address).GetAddressBytes();

    private static ConnectionRow TcpRow(int pid, string local, ushort localPort, string remote, ushort remotePort,
        TcpConnectionState state = TcpConnectionState.Established,
        ConnectionProtocol protocol = ConnectionProtocol.TcpV4)
        => new(protocol, local, localPort, remote, remotePort, state, pid);

    private static ConnectionRow UdpRow(int pid, string local, ushort localPort)
        => new(ConnectionProtocol.UdpV4, local, localPort, "0.0.0.0", 0, TcpConnectionState.None, pid);

    [Fact]
    public void A_forward_keyed_flow_lands_on_its_row()
    {
        var aggregator = new TrafficAggregator();
        aggregator.Add(FlowKey.ForTcp(false, 100, Bytes("10.0.0.1"), 1000, Bytes("10.0.0.2"), 80), sent: true, 4096);
        aggregator.Add(FlowKey.ForTcp(false, 100, Bytes("10.0.0.1"), 1000, Bytes("10.0.0.2"), 80), sent: false, 2048);
        var rates = new ConnectionRates(aggregator.Drain(), elapsedSeconds: 2);

        (double sent, double received) = rates.Lookup(TcpRow(100, "10.0.0.1", 1000, "10.0.0.2", 80));

        Assert.Equal(2048, sent);
        Assert.Equal(1024, received);
    }

    [Fact]
    public void A_swapped_keyed_flow_still_lands_on_the_row()
    {
        var aggregator = new TrafficAggregator();
        // ETW 側が (リモート, ローカル) の向きで積んだ場合
        aggregator.Add(FlowKey.ForTcp(false, 100, Bytes("10.0.0.2"), 80, Bytes("10.0.0.1"), 1000), sent: false, 1000);
        var rates = new ConnectionRates(aggregator.Drain(), elapsedSeconds: 1);

        (double sent, double received) = rates.Lookup(TcpRow(100, "10.0.0.1", 1000, "10.0.0.2", 80));

        Assert.Equal(0, sent);
        Assert.Equal(1000, received);
    }

    [Fact]
    public void Both_orientations_are_summed()
    {
        var aggregator = new TrafficAggregator();
        aggregator.Add(FlowKey.ForTcp(false, 100, Bytes("10.0.0.1"), 1000, Bytes("10.0.0.2"), 80), sent: true, 300);
        aggregator.Add(FlowKey.ForTcp(false, 100, Bytes("10.0.0.2"), 80, Bytes("10.0.0.1"), 1000), sent: true, 200);
        var rates = new ConnectionRates(aggregator.Drain(), elapsedSeconds: 1);

        (double sent, _) = rates.Lookup(TcpRow(100, "10.0.0.1", 1000, "10.0.0.2", 80));

        Assert.Equal(500, sent);
    }

    [Fact]
    public void Udp_rows_match_on_the_folded_local_endpoint()
    {
        var aggregator = new TrafficAggregator();
        // NetTraceSession は UDP イベントを両端点の畳みキーで積む
        aggregator.Add(FlowKey.ForUdp(false, 100, Bytes("192.168.1.10"), 53), sent: false, 700);
        aggregator.Add(FlowKey.ForUdp(false, 100, Bytes("8.8.8.8"), 40000), sent: false, 700);
        var rates = new ConnectionRates(aggregator.Drain(), elapsedSeconds: 1);

        (double sent, double received) = rates.Lookup(UdpRow(100, "192.168.1.10", 53));

        Assert.Equal(0, sent);
        Assert.Equal(700, received);
    }

    [Fact]
    public void Loopback_rows_of_different_processes_stay_separate()
    {
        var aggregator = new TrafficAggregator();
        aggregator.Add(FlowKey.ForTcp(false, 100, Bytes("127.0.0.1"), 5000, Bytes("127.0.0.1"), 6000), sent: true, 111);
        aggregator.Add(FlowKey.ForTcp(false, 200, Bytes("127.0.0.1"), 6000, Bytes("127.0.0.1"), 5000), sent: false, 222);
        var rates = new ConnectionRates(aggregator.Drain(), elapsedSeconds: 1);

        (double clientSent, double clientReceived) = rates.Lookup(TcpRow(100, "127.0.0.1", 5000, "127.0.0.1", 6000));
        (double serverSent, double serverReceived) = rates.Lookup(TcpRow(200, "127.0.0.1", 6000, "127.0.0.1", 5000));

        Assert.Equal((111d, 0d), (clientSent, clientReceived));
        Assert.Equal((0d, 222d), (serverSent, serverReceived));
    }

    [Fact]
    public void Listen_rows_never_carry_traffic()
    {
        var aggregator = new TrafficAggregator();
        aggregator.Add(FlowKey.ForTcp(false, 100, Bytes("0.0.0.0"), 8080, Bytes("0.0.0.0"), 0), sent: false, 999);
        var rates = new ConnectionRates(aggregator.Drain(), elapsedSeconds: 1);

        (double sent, double received) = rates.Lookup(
            TcpRow(100, "0.0.0.0", 8080, "0.0.0.0", 0, TcpConnectionState.Listen));

        Assert.Equal((0d, 0d), (sent, received));
    }

    [Fact]
    public void Scoped_v6_rows_match_scopeless_event_keys()
    {
        var aggregator = new TrafficAggregator();
        aggregator.Add(FlowKey.ForTcp(true, 100, Bytes("fe80::1"), 445, Bytes("fe80::2"), 50000), sent: true, 640);
        var rates = new ConnectionRates(aggregator.Drain(), elapsedSeconds: 1);

        (double sent, _) = rates.Lookup(
            TcpRow(100, "fe80::1%12", 445, "fe80::2%12", 50000, protocol: ConnectionProtocol.TcpV6));

        Assert.Equal(640, sent);
    }

    [Fact]
    public void Unknown_flows_and_zero_elapsed_return_zero()
    {
        var rates = new ConnectionRates(new Dictionary<FlowKey, FlowTotals>(), elapsedSeconds: 1);
        Assert.Equal((0d, 0d), rates.Lookup(TcpRow(100, "10.0.0.1", 1000, "10.0.0.2", 80)));

        var aggregator = new TrafficAggregator();
        aggregator.Add(FlowKey.ForUdp(false, 100, Bytes("10.0.0.1"), 53), sent: true, 5);
        var zeroElapsed = new ConnectionRates(aggregator.Drain(), elapsedSeconds: 0);
        Assert.Equal((0d, 0d), zeroElapsed.Lookup(UdpRow(100, "10.0.0.1", 53)));
    }
}
