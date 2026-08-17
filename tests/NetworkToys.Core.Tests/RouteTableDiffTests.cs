using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

public class RouteTableDiffTests
{
    private static RouteTableSummary CompareText(string before, string after)
        => RouteTableDiff.Compare(CiscoRouteParser.Parse(before), CiscoRouteParser.Parse(after));

    [Fact]
    public void The_same_table_has_no_changes()
    {
        const string text = """
            S*    0.0.0.0/0 [1/0] via 192.168.1.1
            O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0
            """;

        Assert.False(CompareText(text, text).HasChanges);
    }

    [Fact]
    public void Only_the_uptime_differing_is_not_a_change()
    {
        // これができないと、何も変えていないのに全ての動的経路が差分として出る
        const string before = "O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0";
        const string after = "O     10.1.1.0/24 [110/2] via 10.0.0.1, 02:41:07, GigabitEthernet0/0";

        Assert.False(CompareText(before, after).HasChanges);
    }

    [Fact]
    public void A_lost_route_is_reported()
    {
        const string before = """
            O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0
            O     10.2.2.0/24 [110/3] via 10.0.0.1, 00:15:23, GigabitEthernet0/0
            """;
        const string after = "O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:20:00, GigabitEthernet0/0";

        RouteTableSummary summary = CompareText(before, after);

        RouteTableChange change = Assert.Single(summary.Changes);
        Assert.Equal(RouteChangeKind.Removed, change.Kind);
        Assert.Equal("10.2.2.0/24", change.Prefix);
        Assert.Equal(1, summary.Removed);
    }

    [Fact]
    public void A_new_route_is_reported()
    {
        const string before = "O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0";
        const string after = """
            O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0
            B     172.16.0.0/16 [20/0] via 10.0.0.9, 1d02h
            """;

        RouteTableChange change = Assert.Single(CompareText(before, after).Changes);

        Assert.Equal(RouteChangeKind.Added, change.Kind);
        Assert.Equal("172.16.0.0/16", change.Prefix);
    }

    [Fact]
    public void A_changed_next_hop_is_reported()
    {
        // 経路が別の方向へ向いた。切替作業で最も見たい変化
        const string before = "S*    0.0.0.0/0 [1/0] via 192.168.1.1";
        const string after = "S*    0.0.0.0/0 [1/0] via 192.168.1.254";

        RouteTableChange change = Assert.Single(CompareText(before, after).Changes);

        Assert.Equal(RouteChangeKind.NextHopChanged, change.Kind);
        Assert.Contains("192.168.1.254", change.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_protocol_is_reported()
    {
        const string before = "O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0";
        const string after = "S     10.1.1.0/24 [1/0] via 10.0.0.1";

        RouteTableChange change = Assert.Single(CompareText(before, after).Changes);

        Assert.Equal(RouteChangeKind.ProtocolChanged, change.Kind);
    }

    [Fact]
    public void A_changed_metric_is_reported()
    {
        const string before = "O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0";
        const string after = "O     10.1.1.0/24 [110/20] via 10.0.0.1, 00:15:23, GigabitEthernet0/0";

        RouteTableChange change = Assert.Single(CompareText(before, after).Changes);

        Assert.Equal(RouteChangeKind.MetricChanged, change.Kind);
    }

    [Fact]
    public void Equal_cost_paths_in_a_different_order_are_the_same()
    {
        const string before = """
            O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0
                              [110/2] via 10.0.0.5, 00:15:23, GigabitEthernet0/1
            """;
        const string after = """
            O     10.1.1.0/24 [110/2] via 10.0.0.5, 00:20:00, GigabitEthernet0/1
                              [110/2] via 10.0.0.1, 00:20:00, GigabitEthernet0/0
            """;

        Assert.False(CompareText(before, after).HasChanges);
    }

    [Fact]
    public void Losing_one_of_two_paths_is_a_change()
    {
        const string before = """
            O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0
                              [110/2] via 10.0.0.5, 00:15:23, GigabitEthernet0/1
            """;
        const string after = "O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:20:00, GigabitEthernet0/0";

        RouteTableChange change = Assert.Single(CompareText(before, after).Changes);

        Assert.Equal(RouteChangeKind.NextHopChanged, change.Kind);
    }

    [Fact]
    public void Lost_routes_are_listed_first()
    {
        const string before = """
            O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0
            O     10.2.2.0/24 [110/3] via 10.0.0.1, 00:15:23, GigabitEthernet0/0
            """;
        const string after = """
            O     10.1.1.0/24 [110/9] via 10.0.0.1, 00:15:23, GigabitEthernet0/0
            B     172.16.0.0/16 [20/0] via 10.0.0.9, 1d02h
            """;

        RouteTableSummary summary = CompareText(before, after);

        Assert.Equal(RouteChangeKind.Removed, summary.Changes[0].Kind);
        Assert.Equal(3, summary.Changes.Count);
    }

    [Fact]
    public void The_headline_counts_the_routes()
    {
        const string before = "O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0";
        const string after = """
            O     10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0
            B     172.16.0.0/16 [20/0] via 10.0.0.9, 1d02h
            """;

        RouteTableSummary summary = CompareText(before, after);

        Assert.Contains("1 → 2 本", summary.Headline, StringComparison.Ordinal);
    }
}
