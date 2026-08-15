using PastelNet.Core.Work;
using Xunit;

namespace PastelNet.Core.Tests;

public class MacTableTests
{
    private const string Sample = """
              Mac Address Table
        -------------------------------------------

        Vlan    Mac Address       Type        Ports
        ----    -----------       --------    -----
           1    0011.2233.4455    DYNAMIC     Gi0/1
           1    0011.2233.6677    DYNAMIC     Gi0/2
          10    aabb.ccdd.eeff    STATIC      Gi0/24
        Total Mac Addresses for this criterion: 3
        """;

    [Fact]
    public void The_header_and_rules_are_skipped()
    {
        IReadOnlyList<MacTableEntry> entries = MacTableParser.Parse(Sample);

        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void An_entry_is_read()
    {
        MacTableEntry entry = Assert.Single(MacTableParser.Parse(Sample), e => e.Port == "Gi0/1");

        Assert.Equal("1", entry.Vlan);
        Assert.Equal("0011.2233.4455", entry.Mac);
        Assert.True(entry.IsDynamic);
    }

    [Fact]
    public void A_static_entry_is_not_dynamic()
    {
        MacTableEntry entry = Assert.Single(MacTableParser.Parse(Sample), e => e.Vlan == "10");

        Assert.False(entry.IsDynamic);
    }

    [Theory]
    [InlineData("   1    00:11:22:33:44:55    DYNAMIC     Gi0/1")]
    [InlineData("   1    00-11-22-33-44-55    DYNAMIC     Gi0/1")]
    public void Other_mac_notations_are_read_too(string line)
    {
        Assert.Single(MacTableParser.Parse(line));
    }

    [Fact]
    public void The_same_table_shows_no_change()
    {
        Assert.False(MacTableDiff.Compare(Sample, Sample).HasChanges);
    }

    [Fact]
    public void A_mac_moving_to_another_port_is_critical()
    {
        // これが見たいもの。機器の差し替えやループの兆候
        const string after = """
            Vlan    Mac Address       Type        Ports
               1    0011.2233.4455    DYNAMIC     Gi0/5
               1    0011.2233.6677    DYNAMIC     Gi0/2
              10    aabb.ccdd.eeff    STATIC      Gi0/24
            """;

        DeviceChange change = Assert.Single(MacTableDiff.Compare(Sample, after).Changes);

        Assert.Equal("ポートが移った", change.Kind);
        Assert.Equal(ChangeSeverity.Critical, change.Severity);
        Assert.Contains("Gi0/5", change.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dynamic_entry_disappearing_is_only_information()
    {
        // 通信が無ければ数分で消える。作業と関係なく起きるので重く扱わない
        const string after = """
            Vlan    Mac Address       Type        Ports
               1    0011.2233.6677    DYNAMIC     Gi0/2
              10    aabb.ccdd.eeff    STATIC      Gi0/24
            """;

        DeviceChange change = Assert.Single(MacTableDiff.Compare(Sample, after).Changes);

        Assert.Equal("消えた", change.Kind);
        Assert.Equal(ChangeSeverity.Info, change.Severity);
    }

    [Fact]
    public void A_static_entry_disappearing_is_worth_a_look()
    {
        const string after = """
            Vlan    Mac Address       Type        Ports
               1    0011.2233.4455    DYNAMIC     Gi0/1
               1    0011.2233.6677    DYNAMIC     Gi0/2
            """;

        DeviceChange change = Assert.Single(MacTableDiff.Compare(Sample, after).Changes);

        Assert.Equal(ChangeSeverity.Warning, change.Severity);
    }

    [Fact]
    public void The_same_mac_in_another_vlan_is_a_different_entry()
    {
        const string before = "   1    0011.2233.4455    DYNAMIC     Gi0/1";
        const string after = "  20    0011.2233.4455    DYNAMIC     Gi0/1";

        DeviceCompareOutcome outcome = MacTableDiff.Compare(before, after);

        Assert.Equal(2, outcome.Changes.Count);   // 片方が消えて、片方が現れた
    }

    [Fact]
    public void The_note_explains_why_the_counts_move_on_their_own()
    {
        DeviceCompareOutcome outcome = MacTableDiff.Compare(Sample, Sample);

        Assert.NotNull(outcome.Note);
        Assert.Contains("ポートが移った", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_headline_says_when_nothing_moved()
    {
        Assert.Contains("ポートの移動はありません", MacTableDiff.Compare(Sample, Sample).Headline, StringComparison.Ordinal);
    }
}
