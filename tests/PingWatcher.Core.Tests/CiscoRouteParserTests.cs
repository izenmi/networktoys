using PingWatcher.Core.Work;
using Xunit;

namespace PingWatcher.Core.Tests;

public class CiscoRouteParserTests
{
    private const string Sample = """
        Codes: L - local, C - connected, S - static, R - RIP, M - mobile, B - BGP
               D - EIGRP, EX - EIGRP external, O - OSPF, IA - OSPF inter area

        Gateway of last resort is 192.168.1.1 to network 0.0.0.0

        S*    0.0.0.0/0 [1/0] via 192.168.1.1
              10.0.0.0/8 is variably subnetted, 3 subnets, 2 masks
        C        10.0.0.0/30 is directly connected, GigabitEthernet0/0
        L        10.0.0.2/32 is directly connected, GigabitEthernet0/0
        O        10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0
        B     172.16.0.0/16 [20/0] via 10.0.0.1, 2d10h
        """;

    [Fact]
    public void Empty_input_yields_nothing()
    {
        Assert.Empty(CiscoRouteParser.Parse(null));
        Assert.Empty(CiscoRouteParser.Parse("   "));
    }

    [Fact]
    public void The_legend_and_headers_are_skipped()
    {
        IReadOnlyList<CiscoRoute> routes = CiscoRouteParser.Parse(Sample);

        Assert.DoesNotContain(routes, r => r.Prefix.Contains("Codes", StringComparison.Ordinal));
        Assert.Equal(5, routes.Count);
    }

    [Fact]
    public void The_subnetted_notice_is_not_a_route()
    {
        // "10.0.0.0/8 is variably subnetted" は経路ではなく説明行
        IReadOnlyList<CiscoRoute> routes = CiscoRouteParser.Parse(Sample);

        Assert.DoesNotContain(routes, r => r.Prefix == "10.0.0.0/8");
    }

    [Fact]
    public void A_default_route_is_read()
    {
        CiscoRoute route = Assert.Single(CiscoRouteParser.Parse(Sample), r => r.Prefix == "0.0.0.0/0");

        Assert.Equal("S*", route.Protocol);
        Assert.Equal(1, route.AdminDistance);
        Assert.Equal(0, route.Metric);
        Assert.Equal("192.168.1.1", Assert.Single(route.NextHops));
    }

    [Fact]
    public void A_connected_route_is_read()
    {
        CiscoRoute route = Assert.Single(CiscoRouteParser.Parse(Sample), r => r.Prefix == "10.0.0.0/30");

        Assert.Equal("C", route.Protocol);
        Assert.Empty(route.NextHops);
        Assert.Equal("GigabitEthernet0/0", Assert.Single(route.Interfaces));
    }

    [Fact]
    public void An_ospf_route_is_read_without_its_uptime()
    {
        CiscoRoute route = Assert.Single(CiscoRouteParser.Parse(Sample), r => r.Prefix == "10.1.1.0/24");

        Assert.Equal("O", route.Protocol);
        Assert.Equal(110, route.AdminDistance);
        Assert.Equal(2, route.Metric);
        Assert.Equal("10.0.0.1", Assert.Single(route.NextHops));
        Assert.Equal("GigabitEthernet0/0", Assert.Single(route.Interfaces));
    }

    [Fact]
    public void Equal_cost_paths_are_gathered_into_one_route()
    {
        const string text = """
            O        10.1.1.0/24 [110/2] via 10.0.0.1, 00:15:23, GigabitEthernet0/0
                                 [110/2] via 10.0.0.5, 00:15:23, GigabitEthernet0/1
            """;

        CiscoRoute route = Assert.Single(CiscoRouteParser.Parse(text));

        Assert.Equal(2, route.NextHops.Count);
        Assert.Contains("10.0.0.1", route.NextHops);
        Assert.Contains("10.0.0.5", route.NextHops);
    }

    [Fact]
    public void Multi_word_codes_are_kept_together()
    {
        const string text = """
            O IA     10.2.0.0/24 [110/3] via 10.0.0.1, 01:02:03, GigabitEthernet0/0
            D EX     10.3.0.0/24 [170/5] via 10.0.0.9, 00:00:11, GigabitEthernet0/2
            """;

        IReadOnlyList<CiscoRoute> routes = CiscoRouteParser.Parse(text);

        Assert.Equal("O IA", routes[0].Protocol);
        Assert.Equal("D EX", routes[1].Protocol);
    }

    [Fact]
    public void A_classful_route_without_a_mask_is_still_read()
    {
        const string text = "C    192.168.1.0 is directly connected, Vlan1";

        CiscoRoute route = Assert.Single(CiscoRouteParser.Parse(text));

        Assert.Equal("192.168.1.0", route.Prefix);
        Assert.Equal("Vlan1", Assert.Single(route.Interfaces));
    }

    [Fact]
    public void The_uptime_never_reaches_the_parsed_route()
    {
        // ここが構造化する理由そのもの。経過時間は毎回変わるので比較に使えない
        IReadOnlyList<CiscoRoute> routes = CiscoRouteParser.Parse(Sample);

        Assert.All(routes, r =>
        {
            Assert.DoesNotContain("00:15:23", r.NextHopText, StringComparison.Ordinal);
            Assert.DoesNotContain("2d10h", r.NextHopText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Huge_metric_digits_do_not_throw()
    {
        // 貼り付けは壊れたログのこともある。桁あふれで例外を投げない
        const string text = "O    10.0.0.0/8 [110/99999999999] via 10.0.0.1, 00:00:05, Gi0/1";

        CiscoRoute route = Assert.Single(CiscoRouteParser.Parse(text));

        Assert.Equal(110, route.AdminDistance);
        Assert.Null(route.Metric);
    }

    [Fact]
    public void A_static_ecmp_continuation_without_distance_is_kept()
    {
        // スタティックの等コスト経路は 2 本目以降を距離なしで印字する
        const string text = "S    10.0.0.0/8 [1/0] via 192.168.1.1\n          via 192.168.1.2";

        CiscoRoute route = Assert.Single(CiscoRouteParser.Parse(text));

        Assert.Equal(2, route.NextHops.Count);
        Assert.Equal("192.168.1.2", route.NextHops[1]);
    }

    [Fact]
    public void A_subnetted_header_lends_its_mask_to_children()
    {
        // 配下の行はマスクを省いて印字される。補わないと、前後で classful 表記と
        // /24 付き表記が混ざったとき同じ経路が「消えた」+「増えた」に化ける
        const string text = "     172.16.0.0/24 is subnetted, 2 subnets\nO       172.16.1.0 [110/2] via 10.0.0.1, 00:00:05, Gi0/1";

        CiscoRoute route = Assert.Single(CiscoRouteParser.Parse(text));

        Assert.Equal("172.16.1.0/24", route.Prefix);
    }
}
