using PingWatcher.Core.Net;
using Xunit;

namespace PingWatcher.Core.Tests;

public class ConnectionTableViewTests
{
    private static ConnectionRow Tcp(
        string local, ushort localPort, string remote, ushort remotePort,
        TcpConnectionState state, int pid, ConnectionProtocol protocol = ConnectionProtocol.TcpV4)
        => new(protocol, local, localPort, remote, remotePort, state, pid);

    private static ConnectionRow Udp(string local, ushort localPort, int pid, bool v6 = false)
        => new(v6 ? ConnectionProtocol.UdpV6 : ConnectionProtocol.UdpV4,
            local, localPort, v6 ? "::" : "0.0.0.0", 0, TcpConnectionState.None, pid);

    private static readonly Dictionary<int, string> Names = new()
    {
        [100] = "Alpha",
        [200] = "chrome",
    };

    [Theory]
    [InlineData(0x901Fu, 8080)]   // 0x1F90 = 8080 のネットワークオーダー表現
    [InlineData(0xBB01u, 443)]
    [InlineData(0u, 0)]
    [InlineData(0xFFFFu, 65535)]
    public void Ports_come_back_from_network_order(uint raw, int expected)
    {
        Assert.Equal((ushort)expected, ConnectionTableView.PortFromNetworkOrder(raw));
    }

    [Fact]
    public void Every_tcp_state_has_a_text()
    {
        foreach (TcpConnectionState state in Enum.GetValues<TcpConnectionState>())
        {
            (string text, _) = TcpStateText.Describe(state);
            Assert.False(string.IsNullOrEmpty(text));
        }
    }

    [Fact]
    public void Steady_states_carry_symbols_and_kinds()
    {
        Assert.Equal(("● ESTABLISHED", ConnectionStateKind.Ok), TcpStateText.Describe(TcpConnectionState.Established));
        Assert.Equal(("⊘ LISTEN", ConnectionStateKind.Info), TcpStateText.Describe(TcpConnectionState.Listen));
        Assert.Equal(("TIME_WAIT", ConnectionStateKind.Muted), TcpStateText.Describe(TcpConnectionState.TimeWait));
        Assert.Equal(("—", ConnectionStateKind.Muted), TcpStateText.Describe(TcpConnectionState.None));
    }

    [Fact]
    public void Groups_are_ordered_by_name_then_pid()
    {
        var rows = new[]
        {
            Udp("0.0.0.0", 500, 300),                                                 // 未解決 → "PID 300"
            Tcp("10.0.0.1", 1000, "10.0.0.2", 80, TcpConnectionState.Established, 200),
            Tcp("10.0.0.1", 1001, "10.0.0.2", 80, TcpConnectionState.Established, 100),
            Udp("0.0.0.0", 501, 201),                                                 // "chrome" の別 PID
        };
        var names = new Dictionary<int, string>(Names) { [201] = "chrome" };

        IReadOnlyList<ConnectionListRow> built = ConnectionTableView.BuildRows(rows, names, null, null);

        string[] groupNames = built.OfType<ConnectionGroupRow>().Select(g => g.ProcessName).ToArray();
        Assert.Equal(new[] { "Alpha", "chrome", "chrome", "PID 300" }, groupNames);

        string[] pidTexts = built.OfType<ConnectionGroupRow>().Select(g => g.PidText).ToArray();
        Assert.Equal(new[] { "PID 100", "PID 200", "PID 201", "PID 300" }, pidTexts);
    }

    [Fact]
    public void Rows_inside_a_group_are_ordered_by_protocol_then_port()
    {
        var rows = new[]
        {
            Udp("0.0.0.0", 53, 100),
            Tcp("10.0.0.1", 2000, "10.0.0.2", 80, TcpConnectionState.Established, 100),
            Tcp("::1", 1000, "::1", 445, TcpConnectionState.Established, 100, ConnectionProtocol.TcpV6),
            Tcp("10.0.0.1", 1000, "10.0.0.2", 80, TcpConnectionState.Established, 100),
        };

        IReadOnlyList<ConnectionListRow> built = ConnectionTableView.BuildRows(rows, Names, null, null);

        string[] protocols = built.OfType<ConnectionDetailRow>().Select(d => d.Protocol).ToArray();
        Assert.Equal(new[] { "TCP", "TCP", "TCPv6", "UDP" }, protocols);

        var tcpRows = built.OfType<ConnectionDetailRow>().Where(d => d.Protocol == "TCP").ToArray();
        Assert.Equal("10.0.0.1:1000", tcpRows[0].Local);
        Assert.Equal("10.0.0.1:2000", tcpRows[1].Local);
    }

    [Fact]
    public void Header_count_matches_rows_and_pid_zero_has_no_pid_text()
    {
        var rows = new[]
        {
            Tcp("10.0.0.1", 1000, "10.0.0.2", 80, TcpConnectionState.TimeWait, 0),
            Tcp("10.0.0.1", 1001, "10.0.0.2", 80, TcpConnectionState.TimeWait, 0),
        };

        IReadOnlyList<ConnectionListRow> built = ConnectionTableView.BuildRows(rows, Names, null, null);

        ConnectionGroupRow group = Assert.IsType<ConnectionGroupRow>(built[0]);
        Assert.Equal("(所有プロセスなし)", group.ProcessName);
        Assert.Equal("", group.PidText);
        Assert.Equal("2 件", group.CountText);
        Assert.Equal(3, built.Count);
    }

    [Fact]
    public void Listen_and_udp_have_no_remote()
    {
        var rows = new[]
        {
            Tcp("0.0.0.0", 8080, "0.0.0.0", 0, TcpConnectionState.Listen, 100),
            Udp("0.0.0.0", 53, 100),
        };

        var details = ConnectionTableView.BuildRows(rows, Names, null, null).OfType<ConnectionDetailRow>().ToArray();

        Assert.All(details, d => Assert.Equal("—", d.Remote));
    }

    [Fact]
    public void Ipv6_endpoints_are_bracketed()
    {
        var rows = new[]
        {
            Tcp("fe80::1%12", 445, "fe80::2%12", 50000, TcpConnectionState.Established, 100, ConnectionProtocol.TcpV6),
        };

        ConnectionDetailRow detail = ConnectionTableView.BuildRows(rows, Names, null, null)
            .OfType<ConnectionDetailRow>().Single();

        Assert.Equal("[fe80::1%12]:445", detail.Local);
        Assert.Equal("[fe80::2%12]:50000", detail.Remote);
    }

    [Fact]
    public void Filter_on_process_name_keeps_the_whole_group()
    {
        var rows = new[]
        {
            Tcp("10.0.0.1", 1000, "10.0.0.2", 80, TcpConnectionState.Established, 200),
            Udp("0.0.0.0", 53, 200),
            Tcp("10.0.0.1", 1001, "10.0.0.2", 80, TcpConnectionState.Established, 100),
        };

        IReadOnlyList<ConnectionListRow> built = ConnectionTableView.BuildRows(rows, Names, "CHROME", null);

        ConnectionGroupRow group = Assert.IsType<ConnectionGroupRow>(built[0]);
        Assert.Equal("chrome", group.ProcessName);
        Assert.Equal("2 件", group.CountText);
        Assert.Equal(3, built.Count);
    }

    [Fact]
    public void Filter_on_port_keeps_matching_rows_and_recounts()
    {
        var rows = new[]
        {
            Tcp("10.0.0.1", 1000, "93.184.216.34", 443, TcpConnectionState.Established, 200),
            Tcp("10.0.0.1", 1001, "10.0.0.2", 80, TcpConnectionState.Established, 200),
            Udp("0.0.0.0", 53, 100),
        };

        IReadOnlyList<ConnectionListRow> built = ConnectionTableView.BuildRows(rows, Names, "443", null);

        ConnectionGroupRow group = Assert.IsType<ConnectionGroupRow>(built[0]);
        Assert.Equal("1 件", group.CountText);
        ConnectionDetailRow detail = Assert.IsType<ConnectionDetailRow>(built[1]);
        Assert.Equal("93.184.216.34:443", detail.Remote);
        Assert.Equal(2, built.Count);
    }

    [Fact]
    public void Filter_without_match_yields_nothing_and_empty_filter_yields_all()
    {
        var rows = new[] { Udp("0.0.0.0", 53, 100) };

        Assert.Empty(ConnectionTableView.BuildRows(rows, Names, "zzz", null));
        Assert.Equal(2, ConnectionTableView.BuildRows(rows, Names, "", null).Count);
        Assert.Equal(2, ConnectionTableView.BuildRows(rows, Names, null, null).Count);
    }

    [Fact]
    public void Duplicate_udp_endpoints_still_get_unique_keys()
    {
        var rows = new[] { Udp("0.0.0.0", 5000, 100), Udp("0.0.0.0", 5000, 100) };

        var keys = ConnectionTableView.BuildRows(rows, Names, null, null)
            .Select(r => r.SortKey).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Fallback_names_cover_special_pids()
    {
        var empty = new Dictionary<int, string>();
        Assert.Equal("(所有プロセスなし)", ConnectionTableView.ResolveProcessName(0, empty));
        Assert.Equal("System", ConnectionTableView.ResolveProcessName(4, empty));
        Assert.Equal("PID 123", ConnectionTableView.ResolveProcessName(123, empty));
        Assert.Equal("Alpha", ConnectionTableView.ResolveProcessName(100, Names));
    }

    [Fact]
    public void Count_totals_tcp_udp_and_processes()
    {
        var rows = new[]
        {
            Tcp("10.0.0.1", 1000, "10.0.0.2", 80, TcpConnectionState.Established, 100),
            Tcp("::1", 1001, "::1", 445, TcpConnectionState.Established, 100, ConnectionProtocol.TcpV6),
            Udp("0.0.0.0", 53, 200),
        };

        Assert.Equal((2, 1, 2), ConnectionTableView.Count(rows));
    }

    [Fact]
    public void Without_rates_the_traffic_columns_show_a_dash()
    {
        var rows = new[] { Tcp("10.0.0.1", 1000, "10.0.0.2", 80, TcpConnectionState.Established, 100) };

        IReadOnlyList<ConnectionListRow> built = ConnectionTableView.BuildRows(rows, Names, null, null);

        ConnectionGroupRow group = Assert.IsType<ConnectionGroupRow>(built[0]);
        ConnectionDetailRow detail = Assert.IsType<ConnectionDetailRow>(built[1]);
        Assert.Equal("—", group.SentText);
        Assert.Equal("—", detail.ReceivedText);
    }

    [Fact]
    public void Group_traffic_is_the_sum_of_its_rows()
    {
        var rows = new[]
        {
            Tcp("10.0.0.1", 1000, "10.0.0.2", 80, TcpConnectionState.Established, 100),
            Tcp("10.0.0.1", 1001, "10.0.0.3", 80, TcpConnectionState.Established, 100),
        };

        var aggregator = new TrafficAggregator();
        aggregator.Add(TcpKey(100, "10.0.0.1", 1000, "10.0.0.2", 80), sent: true, 2048);
        aggregator.Add(TcpKey(100, "10.0.0.1", 1001, "10.0.0.3", 80), sent: true, 4096);
        var rates = new ConnectionRates(aggregator.Drain(), elapsedSeconds: 1);

        IReadOnlyList<ConnectionListRow> built = ConnectionTableView.BuildRows(rows, Names, null, rates);

        ConnectionGroupRow group = Assert.IsType<ConnectionGroupRow>(built[0]);
        Assert.Equal("6.0 KB/s", group.SentText);
        Assert.Equal("2.0 KB/s", ((ConnectionDetailRow)built[1]).SentText);
        Assert.Equal("4.0 KB/s", ((ConnectionDetailRow)built[2]).SentText);
        Assert.Equal("—", group.ReceivedText);   // 流れていない方向はゼロ = 「—」
    }

    [Fact]
    public void Sorting_by_local_orders_rows_within_the_group()
    {
        var rows = new[]
        {
            Tcp("10.0.0.1", 2000, "10.0.0.2", 80, TcpConnectionState.Established, 100),
            Tcp("10.0.0.1", 1000, "10.0.0.2", 80, TcpConnectionState.Established, 100),
        };

        var ascending = ConnectionTableView.BuildRows(rows, Names, null, null, "Local", false)
            .OfType<ConnectionDetailRow>().Select(d => d.Local).ToArray();
        Assert.Equal(new[] { "10.0.0.1:1000", "10.0.0.1:2000" }, ascending);

        var descending = ConnectionTableView.BuildRows(rows, Names, null, null, "Local", true)
            .OfType<ConnectionDetailRow>().Select(d => d.Local).ToArray();
        Assert.Equal(new[] { "10.0.0.1:2000", "10.0.0.1:1000" }, descending);
    }

    [Fact]
    public void Sorting_by_sent_reorders_groups_by_their_totals()
    {
        var rows = new[]
        {
            Tcp("10.0.0.1", 1000, "10.0.0.2", 80, TcpConnectionState.Established, 100),   // Alpha
            Tcp("10.0.0.1", 2000, "10.0.0.3", 80, TcpConnectionState.Established, 200),   // chrome
        };

        var aggregator = new TrafficAggregator();
        aggregator.Add(TcpKey(100, "10.0.0.1", 1000, "10.0.0.2", 80), sent: true, 100);
        aggregator.Add(TcpKey(200, "10.0.0.1", 2000, "10.0.0.3", 80), sent: true, 5000);
        var rates = new ConnectionRates(aggregator.Drain(), elapsedSeconds: 1);

        string[] groupNames = ConnectionTableView.BuildRows(rows, Names, null, rates, "Sent", true)
            .OfType<ConnectionGroupRow>().Select(g => g.ProcessName).ToArray();

        Assert.Equal(new[] { "chrome", "Alpha" }, groupNames);
    }

    private static FlowKey TcpKey(uint pid, string a, ushort aPort, string b, ushort bPort)
        => FlowKey.ForTcp(false, pid,
            System.Net.IPAddress.Parse(a).GetAddressBytes(), aPort,
            System.Net.IPAddress.Parse(b).GetAddressBytes(), bPort);
}
