using PastelNet.Core.Work;
using Xunit;

namespace PastelNet.Core.Tests;

public class InterfaceBriefTests
{
    private const string Sample = """
        Interface              IP-Address      OK? Method Status                Protocol
        GigabitEthernet0/0     10.0.0.2        YES NVRAM  up                    up
        GigabitEthernet0/1     unassigned      YES NVRAM  administratively down down
        GigabitEthernet0/2     unassigned      YES unset  down                  down
        Vlan1                  192.168.1.1     YES NVRAM  up                    up
        """;

    [Fact]
    public void The_header_is_skipped()
    {
        IReadOnlyList<InterfaceBriefEntry> entries = InterfaceBriefParser.Parse(Sample);

        Assert.Equal(4, entries.Count);
        Assert.DoesNotContain(entries, e => e.Name == "Interface");
    }

    [Fact]
    public void A_status_containing_a_space_does_not_shift_the_columns()
    {
        // "administratively down" は 2 語。前から数えると Protocol を取り違える
        InterfaceBriefEntry entry = Assert.Single(InterfaceBriefParser.Parse(Sample), e => e.Name == "GigabitEthernet0/1");

        Assert.Equal("administratively down", entry.Status);
        Assert.Equal("down", entry.Protocol);
        Assert.True(entry.IsAdministrativelyDown);
    }

    [Fact]
    public void An_up_port_is_recognised()
    {
        InterfaceBriefEntry entry = Assert.Single(InterfaceBriefParser.Parse(Sample), e => e.Name == "GigabitEthernet0/0");

        Assert.True(entry.IsUp);
        Assert.Equal("10.0.0.2", entry.IpAddress);
    }

    [Fact]
    public void A_down_port_is_not_up()
    {
        Assert.All(
            InterfaceBriefParser.Parse(Sample).Where(e => e.Name != "GigabitEthernet0/0" && e.Name != "Vlan1"),
            e => Assert.False(e.IsUp));
    }

    [Fact]
    public void The_same_output_shows_no_change()
    {
        Assert.False(InterfaceBriefDiff.Compare(Sample, Sample).HasChanges);
    }

    [Fact]
    public void A_port_going_down_is_critical()
    {
        const string after = """
            Interface              IP-Address      OK? Method Status                Protocol
            GigabitEthernet0/0     10.0.0.2        YES NVRAM  down                  down
            GigabitEthernet0/1     unassigned      YES NVRAM  administratively down down
            GigabitEthernet0/2     unassigned      YES unset  down                  down
            Vlan1                  192.168.1.1     YES NVRAM  up                    up
            """;

        DeviceCompareOutcome outcome = InterfaceBriefDiff.Compare(Sample, after);
        DeviceChange change = Assert.Single(outcome.Changes);

        Assert.Equal(ChangeSeverity.Critical, change.Severity);
        Assert.Equal("GigabitEthernet0/0", change.Key);
        Assert.Contains("リンクが落ちています", change.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shutdown_port_says_so()
    {
        const string after = """
            Interface              IP-Address      OK? Method Status                Protocol
            GigabitEthernet0/0     10.0.0.2        YES NVRAM  administratively down down
            """;
        const string before = """
            Interface              IP-Address      OK? Method Status                Protocol
            GigabitEthernet0/0     10.0.0.2        YES NVRAM  up                    up
            """;

        DeviceChange change = Assert.Single(InterfaceBriefDiff.Compare(before, after).Changes);

        // 意図的に落としたのか、落ちてしまったのかは言い分ける
        Assert.Contains("shutdown", change.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_port_coming_up_is_only_information()
    {
        const string before = """
            Interface              IP-Address      OK? Method Status                Protocol
            GigabitEthernet0/2     unassigned      YES unset  down                  down
            """;
        const string after = """
            Interface              IP-Address      OK? Method Status                Protocol
            GigabitEthernet0/2     unassigned      YES unset  up                    up
            """;

        DeviceChange change = Assert.Single(InterfaceBriefDiff.Compare(before, after).Changes);

        Assert.Equal(ChangeSeverity.Info, change.Severity);
    }

    [Fact]
    public void A_changed_address_is_reported()
    {
        const string before = """
            Interface              IP-Address      OK? Method Status                Protocol
            Vlan1                  192.168.1.1     YES NVRAM  up                    up
            """;
        const string after = """
            Interface              IP-Address      OK? Method Status                Protocol
            Vlan1                  192.168.1.254   YES NVRAM  up                    up
            """;

        DeviceChange change = Assert.Single(InterfaceBriefDiff.Compare(before, after).Changes);

        Assert.Equal("IP が変わった", change.Kind);
    }

    [Fact]
    public void A_vanished_port_is_reported()
    {
        const string before = """
            Interface              IP-Address      OK? Method Status                Protocol
            GigabitEthernet0/0     10.0.0.2        YES NVRAM  up                    up
            Vlan1                  192.168.1.1     YES NVRAM  up                    up
            """;
        const string after = """
            Interface              IP-Address      OK? Method Status                Protocol
            GigabitEthernet0/0     10.0.0.2        YES NVRAM  up                    up
            """;

        DeviceChange change = Assert.Single(InterfaceBriefDiff.Compare(before, after).Changes);

        Assert.Equal("消えた", change.Kind);
    }

    [Fact]
    public void The_headline_counts_the_up_ports()
    {
        DeviceCompareOutcome outcome = InterfaceBriefDiff.Compare(Sample, Sample);

        Assert.Contains("up 2 本", outcome.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_input_says_so()
    {
        DeviceCompareOutcome outcome = InterfaceBriefDiff.Compare("まったく関係のない文章", "これも関係ない");

        Assert.False(outcome.HasChanges);
        Assert.Contains("読み取れません", outcome.Headline, StringComparison.Ordinal);
    }
}
