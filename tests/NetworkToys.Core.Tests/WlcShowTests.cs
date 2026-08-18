using NetworkToys.Core.Wireless;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// SSH で流した <c>show</c> の出力から表を作れること。
///
/// RESTCONF を有効にできない現場ではこちらが本道なので、
/// <b>桁が動いても読めること</b>を固定しておく（版で列の幅も並びも変わる）。
/// </summary>
public sealed class WlcShowTests
{
    private const string ApSummary = """
        Number of APs: 3

        AP Name    Slots  AP Model   Ethernet MAC    Radio MAC       Location   Country  IP Address    State
        -----------------------------------------------------------------------------------------------------
        AP-1F-01   2      C9120AXI-Q aabb.ccdd.ee01  aabb.ccdd.ef01  1F         JP       10.10.1.11    Registered
        AP-1F-02   2      C9120AXI-Q aabb.ccdd.ee02  aabb.ccdd.ef02  1F         JP       10.10.1.12    Registered
        """;

    private const string JoinStats = """
        Number of APs: 3

        AP Name    Ethernet MAC     Status        Last Successful Join   Last Disconnect Reason
        --------------------------------------------------------------------------------------
        AP-1F-01   aabb.ccdd.ee01   Joined        08/17/2026 09:00:00    Heartbeat timeout
        AP-1F-02   aabb.ccdd.ee02   Joined        08/17/2026 09:01:00    None
        AP-2F-09   aabb.ccdd.ee09   Not Joined    08/10/2026 11:00:00    Link failure
        """;

    private const string WlanSummary = """
        Number of WLANs: 2

        ID   Profile Name      SSID           Status   Security
        -------------------------------------------------------
        1    corp-profile      Corp-WiFi      Enabled  [WPA2][802.1x]
        7    guest-profile     Guest-WiFi     Disabled [WPA2][PSK]
        """;

    // 実機は MAC と AP 名のあいだが空白 1 つになる（ここで列がずれていた）
    private const string ClientSummary = """
        Number of Clients: 2

        MAC Address    AP Name                          Type ID   State        Protocol Method     Role
        --------------------------------------------------------------------------------------------------
        1122.3344.5501 AP-1F-01                         WLAN 1    Run          11ac     Dot1x      Local
        1122.3344.5502 AP-1F-02                         WLAN 7    Run          11ax     None       Local
        """;

    [Fact]
    public void Ap_summary_becomes_rows()
    {
        IReadOnlyList<WlcApRow> rows = WlcShow.ParseApSummary(ApSummary);

        Assert.Equal(2, rows.Count);
        Assert.Equal("AP-1F-01", rows[0].Name);
        Assert.Equal("10.10.1.11", rows[0].Ip);
        Assert.Equal("aabb.ccdd.ee01", rows[0].Mac);
        Assert.Equal("C9120AXI-Q", rows[0].Model);
        Assert.Equal("● 接続", rows[0].State);
        Assert.True(rows[0].IsJoined);
    }

    [Fact]
    public void Aps_that_are_only_in_the_join_records_are_shown_as_missing()
    {
        // 繋がっていない AP を落とすと「その AP は無い」と誤読される
        IReadOnlyList<WlcApRow> rows = WlcShow.ParseApSummary(ApSummary, JoinStats);

        Assert.Equal(3, rows.Count);

        WlcApRow missing = rows.Single(r => r.Name == "AP-2F-09");

        Assert.False(missing.IsJoined);
        Assert.Equal("✕ 未接続", missing.State);
        Assert.Equal("aabb.ccdd.ee09", missing.Mac);
    }

    [Fact]
    public void Join_stats_keep_the_reason_and_the_state()
    {
        IReadOnlyList<WlcJoinRow> rows = WlcShow.ParseJoinStats(JoinStats);

        Assert.Equal(3, rows.Count);
        Assert.Equal("● 接続", rows[0].State);
        Assert.Equal("08/17/2026 09:00:00", rows[0].LastJoin);
        Assert.StartsWith("✕", rows[2].State, StringComparison.Ordinal);
    }

    [Fact]
    public void Wlan_summary_becomes_ssid_rows()
    {
        IReadOnlyList<WlcSsidRow> rows = WlcShow.ParseWlanSummary(WlanSummary);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Corp-WiFi", rows[0].Ssid);
        Assert.Equal("corp-profile", rows[0].Profile);
        Assert.Equal("1", rows[0].Id);
        Assert.Equal("● 有効", rows[0].State);
        Assert.Equal("◌ 無効", rows[1].State);
    }

    [Fact]
    public void Clients_take_their_ssid_from_the_wlan_list()
    {
        IReadOnlyList<WlcSsidRow> wlans = WlcShow.ParseWlanSummary(WlanSummary);
        IReadOnlyList<WlcClientRow> rows = WlcShow.ParseClientSummary(ClientSummary, wlans);

        Assert.Equal(2, rows.Count);

        // MAC の欄に AP 名まで入っていた（2026-08-18 報告）。列は位置で決める
        Assert.Equal("1122.3344.5501", rows[0].Mac);
        Assert.Equal("AP-1F-01", rows[0].ApName);
        Assert.Equal("Corp-WiFi", rows[0].Ssid);
        // 見出しが「Protocol Method」と空白 1 つで並ぶ版がある。1 語目を採る
        Assert.Equal("11ac", rows[0].Radio);
        Assert.Equal("● 通信中", rows[0].State);

        Assert.Equal("Guest-WiFi", rows[1].Ssid);

        // show には出ない項目を 0 で埋めない（「電波が強い」ように見せない）
        Assert.Equal("—", rows[0].RssiText);
        Assert.Equal("", rows[0].Ip);
    }

    [Fact]
    public void Ip_and_vendor_are_filled_from_the_tracking_database_and_the_mac()
    {
        // 見出しの名前は版で違うので当てにしない。MAC に見える語と IP に見える語を拾う
        const string tracking = """
            MAC Address     IP Address      VLAN  AP Name    State
            -------------------------------------------------------
            1122.3344.5501  192.168.20.31   20    AP-1F-01   REACHABLE
            """;

        IReadOnlyDictionary<string, string> ips = WlcShow.ParseIpBindings(tracking);

        IReadOnlyList<WlcClientRow> rows = WlcShow.ParseClientSummary(
            ClientSummary, null, ips, _ => "Apple");

        Assert.Equal("192.168.20.31", rows[0].Ip);
        Assert.Equal("Apple", rows[0].Vendor);

        // 追跡データベースに無い端末は空のまま（0.0.0.0 などで埋めない）
        Assert.Equal("", rows[1].Ip);
    }

    [Fact]
    public void Radio_summary_keeps_channel_and_power_but_not_load()
    {
        const string text = """
            AP Name    Mac Address     Slot  Admin State  Oper State  Width  Txpwr      Channel
            --------------------------------------------------------------------------------------
            AP-1F-01   aabb.ccdd.ef01  1     Enabled      Up          20     1/8 (23 dBm)  36
            """;

        IReadOnlyList<WlcRrmRow> rows = WlcShow.ParseRadioSummary(text, "5GHz");

        Assert.Single(rows);
        Assert.Equal("AP-1F-01", rows[0].ApName);
        Assert.Equal("5GHz", rows[0].Radio);
        Assert.Equal("36", rows[0].Channel);

        // 混み具合と雑音はこの出力に無い。0 ではなく「—」
        Assert.Equal("—", rows[0].UtilizationText);
        Assert.Equal("—", rows[0].Noise);
    }

    [Fact]
    public void Rogue_summary_becomes_rows()
    {
        const string text = """
            Number of Rogues: 1

            MAC Address     Classification  # APs  # Clients  Last Heard            Status
            -------------------------------------------------------------------------------
            00aa.bbcc.dd01  Unclassified    2      0          08/17/2026 10:00:00   Alert
            """;

        IReadOnlyList<WlcRogueRow> rows = WlcShow.ParseRogueSummary(text);

        Assert.Single(rows);
        Assert.Equal("00aa.bbcc.dd01", rows[0].Bssid);
        Assert.Equal("Unclassified", rows[0].Kind);
        Assert.Equal("08/17/2026 10:00:00", rows[0].LastHeard);
    }

    [Fact]
    public void Nothing_readable_gives_an_empty_table_not_an_exception()
    {
        Assert.Empty(WlcShow.ParseApSummary(""));
        Assert.Empty(WlcShow.ParseApSummary("% Invalid input detected at '^' marker."));
        Assert.Empty(WlcShow.ParseClientSummary(null));
    }

    [Fact]
    public void Columns_may_move_between_versions()
    {
        // 語の並びも幅も違う版。見出しから拾えていれば読める
        const string text = """
            AP Name       IP Address     AP Model    Ethernet MAC     State
            ----------------------------------------------------------------
            AP-3F-01      10.10.3.11     C9130AXI    aabb.ccdd.ee31   Registered
            """;

        IReadOnlyList<WlcApRow> rows = WlcShow.ParseApSummary(text);

        Assert.Single(rows);
        Assert.Equal("AP-3F-01", rows[0].Name);
        Assert.Equal("10.10.3.11", rows[0].Ip);
        Assert.Equal("C9130AXI", rows[0].Model);
        Assert.Equal("aabb.ccdd.ee31", rows[0].Mac);
    }
}
