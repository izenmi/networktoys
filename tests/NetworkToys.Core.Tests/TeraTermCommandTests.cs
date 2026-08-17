using NetworkToys.Core.Net;
using Xunit;

namespace NetworkToys.Core.Tests;

public class TeraTermCommandTests
{
    [Fact]
    public void Ssh_uses_port_22_and_forces_ssh2()
        => Assert.Equal("192.168.1.1:22 /ssh /2", TeraTermCommand.Build("192.168.1.1", 0, ssh: true));

    [Fact]
    public void Telnet_uses_port_23_and_opens_in_telnet_mode()
        => Assert.Equal("192.168.1.1:23 /T=1", TeraTermCommand.Build("192.168.1.1", 0, ssh: false));

    [Fact]
    public void An_explicit_port_wins_over_the_default()
    {
        Assert.Equal("sw01:2222 /ssh /2", TeraTermCommand.Build("sw01", 2222, ssh: true));
        Assert.Equal("sw01:992 /T=1", TeraTermCommand.Build("sw01", 992, ssh: false));
    }

    [Fact]
    public void Ipv6_addresses_are_bracketed_so_the_port_is_not_swallowed()
    {
        // 角括弧が無いと "fe80::1:22" の最後の :22 がアドレスの一部に見える
        Assert.Equal("[2001:db8::1]:22 /ssh /2", TeraTermCommand.Build("2001:db8::1", 0, ssh: true));
        Assert.Equal("[2001:db8::1]:22 /ssh /2", TeraTermCommand.Build("[2001:db8::1]", 0, ssh: true));
    }

    [Fact]
    public void An_empty_host_produces_nothing_to_launch()
    {
        Assert.Equal("", TeraTermCommand.Build("", 0, ssh: true));
        Assert.Equal("", TeraTermCommand.Build("   ", 0, ssh: false));
    }

    [Fact]
    public void Surrounding_spaces_are_trimmed()
        => Assert.Equal("host:22 /ssh /2", TeraTermCommand.Build("  host  ", 0, ssh: true));

    [Fact]
    public void Version_5_is_looked_for_before_version_4()
    {
        IReadOnlyList<string> paths = TeraTermCommand.WellKnownPaths(@"C:\PF", @"C:\PFx86");

        Assert.Contains("teraterm5", paths[0]);
        Assert.All(paths, p => Assert.EndsWith("ttermpro.exe", p));
    }
}
