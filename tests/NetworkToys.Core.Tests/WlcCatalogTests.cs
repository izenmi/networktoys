using NetworkToys.Core.Design;
using NetworkToys.Core.Wireless;
using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// WLC の画面が使う小物（絞り込みと CSV）。
///
/// <b>取得は SSH だけ</b>になったので、出力の解釈そのものは
/// <see cref="WlcShowTests"/> が受け持つ。
/// </summary>
public class WlcCatalogTests
{
    private static WlcClientRow Client(string mac, string ip, string ap, string ssid) => new(
        Mac: mac, Ip: ip, Vendor: "", ApName: ap, Ssid: ssid, Radio: "5GHz",
        Rssi: -55, RssiText: "-55", Quality: "良い", Snr: "30", Speed: "866",
        State: "● 通信中", StateKind: SeverityKind.Ok, AssociatedAt: "");

    private static readonly WlcClientRow[] Clients =
    [
        Client("aabb.ccdd.eeff", "192.168.10.20", "AP-1F-01", "Corp-WiFi"),
        Client("1122.3344.5566", "192.168.10.21", "AP-2F-03", "Guest-WiFi"),
    ];

    [Theory]
    [InlineData("192.168.10.20")]
    [InlineData("aabb.ccdd.eeff")]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("aa-bb-cc-dd-ee-ff")]
    [InlineData("AP-1F-01")]
    [InlineData("Corp")]
    public void A_client_can_be_found_however_the_query_was_typed(string query)
    {
        IReadOnlyList<WlcClientRow> found = WlcCatalog.FilterClients(Clients, query);

        Assert.Single(found);
        Assert.Equal("192.168.10.20", found[0].Ip);
    }

    [Fact]
    public void An_empty_query_keeps_every_client()
    {
        Assert.Equal(2, WlcCatalog.FilterClients(Clients, "  ").Count);
        Assert.Equal(2, WlcCatalog.FilterClients(Clients, null).Count);
    }

    [Fact]
    public void Macs_are_compared_without_their_separators()
    {
        Assert.Equal("aabbccddeeff", WlcCatalog.NormalizeMac("AA:BB:CC:DD:EE:FF"));
        Assert.Equal("aabbccddeeff", WlcCatalog.NormalizeMac("aabb.ccdd.eeff"));
        Assert.Equal("", WlcCatalog.NormalizeMac(null));
    }

    [Fact]
    public void Every_table_keeps_one_column_per_header()
    {
        CsvTable[] tables =
        [
            WlcCatalog.ToCsv(Clients),
            WlcCatalog.ToCsv(WlcShow.ParseApSummary("""
                AP Name   AP Model   Ethernet MAC     IP Address    State
                ---------------------------------------------------------
                AP-1F-01  C9120AXI   aabb.ccdd.ee01   10.10.1.11    Registered
                """)),
            WlcCatalog.ToCsv(WlcShow.ParseJoinStats("""
                AP Name   Ethernet MAC     Status   Last Successful Join
                ------------------------------------------------------------
                AP-1F-01  aabb.ccdd.ee01   Joined   08/17/2026 09:00:00
                """)),
            WlcCatalog.ToCsv(WlcShow.ParseRadioSummary("""
                AP Name   Mac Address      Slot  Admin State  Channel
                -----------------------------------------------------
                AP-1F-01  aabb.ccdd.ef01   1     Enabled      36
                """, "5GHz")),
            WlcCatalog.ToCsv(WlcShow.ParseRogueSummary("""
                MAC Address     Classification  Last Heard            Status
                -----------------------------------------------------------------
                00aa.bbcc.dd01  Unclassified    08/17/2026 10:00:00   Alert
                """)),
            WlcCatalog.ToCsv(WlcShow.ParseWlanSummary("""
                ID   Profile Name   SSID        Status
                --------------------------------------
                1    corp-profile   Corp-WiFi   Enabled
                """)),
        ];

        foreach (CsvTable table in tables)
        {
            Assert.NotEmpty(table.Rows);
            Assert.All(table.Rows, row => Assert.Equal(table.Headers.Count, row.Length));
        }
    }
}
