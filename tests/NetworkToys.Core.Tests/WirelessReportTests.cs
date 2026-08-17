using NetworkToys.Core.Metrics;
using NetworkToys.Core.Reporting;
using Xunit;

namespace NetworkToys.Core.Tests;

public class WirelessReportTests
{
    private static readonly WirelessAccessPoint Office = new(
        "office", "AA-BB-CC-11-22-33", "Acme", "-52 dBm", "88%", "36", "5 GHz", IsConnected: true);

    private static readonly WirelessAccessPoint Guest = new(
        "guest", "AA-BB-CC-44-55-66", "Acme", "-70 dBm", "40%", "11", "2.4 GHz", IsConnected: false);

    private static ReportData Data(IReadOnlyList<WirelessAccessPoint>? accessPoints) => new(
        "テスト", new DateTime(2026, 8, 15, 12, 0, 0), "", null, 1000,
        [("IP", "127.0.0.1")],
        [new ReportRow("127.0.0.1", "127.0.0.1", "", "ICMP", RttStatistics.Empty, [])],
        Wireless: [("SSID", "office")],
        WirelessAccessPoints: accessPoints);

    [Fact]
    public void Text_report_lists_access_points_with_a_connected_marker()
    {
        string text = TextReportWriter.Render(Data([Office, Guest]));

        Assert.Contains("周辺のアクセスポイント", text, StringComparison.Ordinal);
        Assert.Contains("* office", text, StringComparison.Ordinal);
        Assert.Contains("AA-BB-CC-44-55-66", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_report_omits_the_table_when_there_was_no_scan()
    {
        string text = TextReportWriter.Render(Data(null));

        Assert.Contains("[無線 LAN]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("周辺のアクセスポイント", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_report_lists_access_points()
    {
        string html = HtmlReportWriter.Render(Data([Office, Guest]));

        Assert.Contains("周辺のアクセスポイント", html, StringComparison.Ordinal);
        Assert.Contains("AA-BB-CC-11-22-33", html, StringComparison.Ordinal);
        Assert.Contains("接続中", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_carries_connection_details_and_access_points()
    {
        string text = WifiSnapshotWriter.Render(
            new DateTime(2026, 8, 15, 12, 34, 56),
            [("SSID", "office"), ("信号", "-52 dBm")],
            [Office, Guest]);

        Assert.Contains("2026/08/15 12:34:56", text, StringComparison.Ordinal);
        Assert.Contains("SSID", text, StringComparison.Ordinal);
        Assert.Contains("周辺のアクセスポイント: 2 件", text, StringComparison.Ordinal);
        Assert.Contains("* office", text, StringComparison.Ordinal);
        Assert.Contains("  guest", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_without_a_connection_still_renders()
    {
        string text = WifiSnapshotWriter.Render(new DateTime(2026, 8, 15, 12, 0, 0), null, []);

        Assert.Contains("周辺のアクセスポイント: 0 件", text, StringComparison.Ordinal);
    }
}
