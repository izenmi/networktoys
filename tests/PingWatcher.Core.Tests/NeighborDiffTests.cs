using PingWatcher.Core.Work;
using Xunit;

namespace PingWatcher.Core.Tests;

public class NeighborDiffTests
{
    private const string Gateway = "192.168.1.1";

    private static Dictionary<string, string> Table(params (string Address, string Mac)[] entries)
        => entries.ToDictionary(e => e.Address, e => e.Mac, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void No_change_yields_nothing()
    {
        var table = Table((Gateway, "00-15-5D-01-02-03"));

        Assert.Empty(NeighborDiff.Compare(table, table, Gateway));
    }

    [Fact]
    public void A_changed_gateway_mac_is_the_headline()
    {
        // 既定ゲートウェイの MAC が変わる = VRRP/HSRP が切り替わった。
        // この用途で最も価値のある一行
        IReadOnlyList<NeighborChange> changes = NeighborDiff.Compare(
            Table((Gateway, "00-00-5E-00-01-01")),
            Table((Gateway, "00-00-5E-00-01-02")),
            Gateway);

        NeighborChange change = Assert.Single(changes);

        Assert.Equal(NeighborChangeKind.MacChanged, change.Kind);
        Assert.True(change.IsGateway);
        Assert.Contains("冗長化", change.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gateway_comes_first()
    {
        IReadOnlyList<NeighborChange> changes = NeighborDiff.Compare(
            Table((Gateway, "AA-AA-AA-AA-AA-AA"), ("192.168.1.50", "BB-BB-BB-BB-BB-BB")),
            Table((Gateway, "CC-CC-CC-CC-CC-CC"), ("192.168.1.50", "DD-DD-DD-DD-DD-DD")),
            Gateway);

        Assert.Equal(Gateway, changes[0].Address);
        Assert.True(changes[0].IsGateway);
    }

    [Fact]
    public void A_disappeared_entry_is_reported()
    {
        IReadOnlyList<NeighborChange> changes = NeighborDiff.Compare(
            Table(("192.168.1.50", "BB-BB-BB-BB-BB-BB")),
            Table(),
            Gateway);

        Assert.Equal(NeighborChangeKind.Disappeared, Assert.Single(changes).Kind);
    }

    [Fact]
    public void A_new_entry_is_reported()
    {
        IReadOnlyList<NeighborChange> changes = NeighborDiff.Compare(
            Table(),
            Table(("192.168.1.50", "BB-BB-BB-BB-BB-BB")),
            Gateway);

        Assert.Equal(NeighborChangeKind.Appeared, Assert.Single(changes).Kind);
    }

    [Fact]
    public void Multicast_entries_are_ignored()
    {
        // 常に居るので比べても意味がない
        IReadOnlyList<NeighborChange> changes = NeighborDiff.Compare(
            Table(("224.0.0.22", "01-00-5E-00-00-16"), ("239.255.255.250", "01-00-5E-7F-FF-FA")),
            Table(),
            Gateway);

        Assert.Empty(changes);
    }

    [Fact]
    public void The_broadcast_entry_is_ignored()
    {
        IReadOnlyList<NeighborChange> changes = NeighborDiff.Compare(
            Table(("255.255.255.255", "FF-FF-FF-FF-FF-FF")),
            Table(),
            Gateway);

        Assert.Empty(changes);
    }

    [Fact]
    public void Case_differences_in_a_mac_are_not_a_change()
    {
        IReadOnlyList<NeighborChange> changes = NeighborDiff.Compare(
            Table((Gateway, "00-15-5d-01-02-03")),
            Table((Gateway, "00-15-5D-01-02-03")),
            Gateway);

        Assert.Empty(changes);
    }

    [Fact]
    public void Entries_are_sorted_by_address()
    {
        IReadOnlyList<NeighborChange> changes = NeighborDiff.Compare(
            Table(),
            Table(("192.168.1.100", "AA-AA-AA-AA-AA-AA"), ("192.168.1.20", "BB-BB-BB-BB-BB-BB")),
            Gateway);

        Assert.Equal("192.168.1.20", changes[0].Address);
        Assert.Equal("192.168.1.100", changes[1].Address);
    }

    [Fact]
    public void Null_input_is_treated_as_empty()
    {
        Assert.Empty(NeighborDiff.Compare(null, null));
        Assert.Single(NeighborDiff.Compare(null, Table(("192.168.1.5", "AA-AA-AA-AA-AA-AA"))));
    }

    [Fact]
    public void Without_a_gateway_nothing_is_flagged_as_one()
    {
        IReadOnlyList<NeighborChange> changes = NeighborDiff.Compare(
            Table((Gateway, "AA-AA-AA-AA-AA-AA")),
            Table((Gateway, "BB-BB-BB-BB-BB-BB")));

        Assert.False(Assert.Single(changes).IsGateway);
    }
}
