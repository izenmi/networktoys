using PingWatcher.Core.Net;
using Xunit;

namespace PingWatcher.Core.Tests;

public class ProxyPlanTests
{
    [Fact]
    public void None_ignores_all_fields()
    {
        ProxyPlan? plan = ProxyPlan.Parse(ProxyMode.None, "ごみ", "ごみ", "ごみ", out string? error);

        Assert.Null(error);
        Assert.Equal(new ProxyPlan(ProxyMode.None, "", "", ""), plan);
    }

    [Theory]
    [InlineData("http://proxy.example.co.jp/proxy.pac")]
    [InlineData("https://proxy.example.co.jp:8080/wpad.dat")]
    public void Valid_pac_urls_pass(string url)
    {
        ProxyPlan? plan = ProxyPlan.Parse(ProxyMode.Pac, url, "", "", out string? error);

        Assert.Null(error);
        Assert.Equal(url, plan!.PacUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("proxy.pac")]
    [InlineData("ftp://proxy/proxy.pac")]
    public void Broken_pac_urls_are_rejected(string url)
    {
        Assert.Null(ProxyPlan.Parse(ProxyMode.Pac, url, "", "", out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Fixed_server_with_port_passes_and_keeps_the_bypass_list()
    {
        ProxyPlan? plan = ProxyPlan.Parse(ProxyMode.Fixed, "", "proxy.example.co.jp:8080", "<local>;10.*", out string? error);

        Assert.Null(error);
        Assert.Equal("proxy.example.co.jp:8080", plan!.Server);
        Assert.Equal("<local>;10.*", plan.Bypass);
    }

    [Fact]
    public void Per_protocol_server_lists_pass_through()
    {
        ProxyPlan? plan = ProxyPlan.Parse(ProxyMode.Fixed, "", "http=p1:8080;https=p2:8443", "", out string? error);

        Assert.Null(error);
        Assert.Equal("http=p1:8080;https=p2:8443", plan!.Server);
    }

    [Theory]
    [InlineData("", "プロキシサーバを入れて")]
    [InlineData("proxy:0", "1〜65535")]
    [InlineData("proxy:70000", "1〜65535")]
    [InlineData("proxy example:8080", "空白")]
    public void Broken_fixed_servers_are_rejected(string server, string expectError)
    {
        Assert.Null(ProxyPlan.Parse(ProxyMode.Fixed, "", server, "", out string? error));
        Assert.Contains(expectError, error ?? "", StringComparison.Ordinal);
    }
}
