using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

public class CdpNeighborTests
{
    private const string Sample = """
        Capability Codes: R - Router, T - Trans Bridge, B - Source Route Bridge
                          S - Switch, H - Host, I - IGMP, r - Repeater

        Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID
        SW02             Gig 0/1           142          S I       WS-C2960  Gig 0/24
        SW03             Gig 0/2           171          S I       WS-C2960  Gig 0/1
        """;

    [Fact]
    public void The_legend_and_header_are_skipped()
    {
        IReadOnlyList<CdpNeighbor> neighbors = CdpNeighborParser.Parse(Sample);

        Assert.Equal(2, neighbors.Count);
        Assert.DoesNotContain(neighbors, n => n.DeviceId.Contains("Capability", StringComparison.Ordinal));
    }

    [Fact]
    public void A_neighbor_is_read()
    {
        CdpNeighbor neighbor = Assert.Single(CdpNeighborParser.Parse(Sample), n => n.DeviceId == "SW02");

        Assert.Equal("Gig 0/1", neighbor.LocalInterface);
        Assert.Equal("Gig 0/24", neighbor.RemotePort);
    }

    [Fact]
    public void The_holdtime_never_reaches_the_result()
    {
        // ここが構造化する理由。Holdtime は数秒ごとに減るので、
        // 残したままだと何も変えていなくても全行が差分になる
        IReadOnlyList<CdpNeighbor> neighbors = CdpNeighborParser.Parse(Sample);

        Assert.All(neighbors, n =>
        {
            Assert.DoesNotContain("142", n.LocalInterface, StringComparison.Ordinal);
            Assert.DoesNotContain("142", n.RemotePort, StringComparison.Ordinal);
            Assert.DoesNotContain("171", n.RemotePort, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Only_the_holdtime_differing_is_not_a_change()
    {
        // 何も変えていなくても Holdtime は減り続ける。ここを無視できないと使えない
        const string before = """
            Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID
            SW02             Gig 0/1           142          S I       WS-C2960  Gig 0/24
            """;
        const string after = """
            Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID
            SW02             Gig 0/1           9            S I       WS-C2960  Gig 0/24
            """;

        Assert.False(CdpNeighborDiff.Compare(before, after).HasChanges);
    }

    [Fact]
    public void A_wrapped_device_name_is_still_read()
    {
        // 機器名が長いと次の行へ折り返す
        const string text = """
            Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID
            very-long-switch-name-01
                             Gig 0/1           142          S I       WS-C2960  Gig 0/24
            """;

        CdpNeighbor neighbor = Assert.Single(CdpNeighborParser.Parse(text));

        Assert.Equal("very-long-switch-name-01", neighbor.DeviceId);
        Assert.Equal("Gig 0/1", neighbor.LocalInterface);
        Assert.Equal("Gig 0/24", neighbor.RemotePort);
    }

    [Fact]
    public void A_lost_neighbor_is_critical()
    {
        const string after = """
            Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID
            SW03             Gig 0/2           171          S I       WS-C2960  Gig 0/1
            """;

        DeviceChange change = Assert.Single(CdpNeighborDiff.Compare(Sample, after).Changes);

        Assert.Equal(ChangeSeverity.Critical, change.Severity);
        Assert.Equal("いなくなった", change.Kind);
    }

    [Fact]
    public void A_cable_moved_to_another_port_is_caught()
    {
        // この対象を入れる一番の理由。挿し直したポートが違っている
        const string before = """
            Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID
            SW02             Gig 0/1           142          S I       WS-C2960  Gig 0/24
            """;
        const string after = """
            Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID
            SW02             Gig 0/1           128          S I       WS-C2960  Gig 0/23
            """;

        DeviceChange change = Assert.Single(CdpNeighborDiff.Compare(before, after).Changes);

        Assert.Equal("接続先のポートが変わった", change.Kind);
        Assert.Equal(ChangeSeverity.Critical, change.Severity);
        Assert.Contains("挿し間違い", change.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_device_on_the_same_port_is_caught()
    {
        const string before = """
            Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID
            SW02             Gig 0/1           142          S I       WS-C2960  Gig 0/24
            """;
        const string after = """
            Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID
            SW99             Gig 0/1           142          S I       WS-C2960  Gig 0/24
            """;

        DeviceChange change = Assert.Single(CdpNeighborDiff.Compare(before, after).Changes);

        Assert.Equal("相手が変わった", change.Kind);
    }

    [Fact]
    public void Port_names_written_with_and_without_a_space_are_the_same_port()
    {
        const string before = """
            Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID
            SW02             Gig 0/1           142          S I       WS-C2960  Gig 0/24
            """;
        const string after = """
            Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID
            SW02             Gig0/1            142          S I       WS-C2960  Gig 0/24
            """;

        Assert.False(CdpNeighborDiff.Compare(before, after).HasChanges);
    }

    [Fact]
    public void A_new_neighbor_is_reported()
    {
        const string before = """
            Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID
            SW02             Gig 0/1           142          S I       WS-C2960  Gig 0/24
            """;

        DeviceChange change = Assert.Single(CdpNeighborDiff.Compare(before, Sample).Changes);

        Assert.Equal("現れた", change.Kind);
    }

    [Fact]
    public void The_note_warns_about_the_refresh_interval()
    {
        DeviceCompareOutcome outcome = CdpNeighborDiff.Compare(Sample, Sample);

        Assert.NotNull(outcome.Note);
        Assert.Contains("再起動", outcome.Note, StringComparison.Ordinal);
    }
}
