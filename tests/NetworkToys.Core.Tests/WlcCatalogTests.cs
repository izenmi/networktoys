using System.Text.Json;
using NetworkToys.Core.Design;
using NetworkToys.Core.Wireless;
using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// C9800 の RESTCONF 応答の読み取り。実機も CI も WLC を持たないので、
/// <b>ここが WLC タブの正しさの拠り所</b>になる。
/// 見本の JSON は WLC が返す形をそのまま縮めたもの。
/// </summary>
public class WlcCatalogTests
{
    // ===== 見本 =====

    private const string CommonJson = """
        {"Cisco-IOS-XE-wireless-client-oper:common-oper-data":[
          {"client-mac":"aabb.ccdd.eeff","ap-name":"AP-1F-01","ms-ap-slot-id":1,"co-state":"client-status-run"},
          {"client-mac":"1122.3344.5566","ap-name":"AP-2F-03","ms-ap-slot-id":0,"co-state":"auth-pending"}
        ]}
        """;

    private const string Dot11Json = """
        {"Cisco-IOS-XE-wireless-client-oper:dot11-oper-data":[
          {"ms-mac-address":"aabb.ccdd.eeff","vap-ssid":"Corp","current-channel":36,
           "radio-type":"dot11-5-ghz","ms-assoc-time":"2026-08-17T09:00:00+09:00"},
          {"ms-mac-address":"1122.3344.5566","vap-ssid":"Guest","current-channel":6,
           "radio-type":"dot11-2-4-ghz"}
        ]}
        """;

    private const string TrafficJson = """
        {"Cisco-IOS-XE-wireless-client-oper:traffic-stats":[
          {"ms-mac-address":"aabb.ccdd.eeff","most-recent-rssi":-58,"most-recent-snr":34,"speed":866},
          {"ms-mac-address":"1122.3344.5566","most-recent-rssi":"-78","most-recent-snr":"12","speed":72}
        ]}
        """;

    private const string SisfJson = """
        {"Cisco-IOS-XE-wireless-client-oper:sisf-db-mac":[
          {"mac-addr":"aabb.ccdd.eeff","ipv4-binding":{"ip-key":{"ip-addr":"192.168.10.50"}}}
        ]}
        """;

    /// <summary>join 済みの AP。<b>無線 MAC と Ethernet MAC が違う</b>のがこの機器の要点。</summary>
    private const string CapwapJson = """
        {"Cisco-IOS-XE-wireless-access-point-oper:capwap-data":[
          {"wtp-mac":"00aa.bb11.2200","name":"AP-1F-01","ip-addr":"10.1.1.11",
           "device-detail":{"static-info":{"board-data":{"wtp-enet-mac":"00aa.bb11.22ff",
             "wtp-serial-num":"FGL1234ABCD"},"ap-models":{"model":"C9130AXI-Q"}},
             "wtp-version":{"sw-version":"17.9.4"}},
           "tag-info":{"policy-tag-info":{"policy-tag-name":"PT-Office"}}}
        ]}
        """;

    private const string RadioJson = """
        {"Cisco-IOS-XE-wireless-access-point-oper:radio-oper-data":[
          {"wtp-mac":"00aa.bb11.2200","radio-slot-id":0,"oper-state":"radio-up",
           "phy-ht-cfg":{"cfg-data":{"curr-freq":1}}},
          {"wtp-mac":"00aa.bb11.2200","radio-slot-id":1,"oper-state":"radio-up",
           "phy-ht-cfg":{"cfg-data":{"curr-freq":36}}}
        ]}
        """;

    /// <summary>設定側は <b>Ethernet の MAC</b> を鍵にしている。</summary>
    private const string ApTagJson = """
        {"Cisco-IOS-XE-wireless-ap-cfg:ap-tag":[
          {"ap-mac":"00aa.bb11.22ff","ap-name":"AP-1F-01","policy-tag":"PT-Office"},
          {"ap-mac":"00cc.dd22.33ff","ap-name":"AP-3F-09","policy-tag":"PT-Office"}
        ]}
        """;

    private const string JoinJson = """
        {"Cisco-IOS-XE-wireless-ap-global-oper:ap-join-stats":[
          {"wtp-mac":"00aa.bb11.2200","ap-name":"AP-1F-01",
           "last-successful-join-time":"2026-08-15T08:00:00+09:00","num-successful-joins":3},
          {"wtp-mac":"00cc.dd22.33ff","ap-join-info":{"ap-name":"AP-3F-09",
           "last-successful-join-time":"2026-08-10T07:30:00+09:00"},
           "ap-disconnect-detail":{"disconnect-time":"2026-08-16T21:15:00+09:00",
             "disconnect-reason-str":"Heartbeat timeout"}}
        ]}
        """;

    private const string RrmJson = """
        {"Cisco-IOS-XE-wireless-rrm-oper:rrm-measurement":[
          {"wtp-mac":"00aa.bb11.2200","radio-slot-id":1,
           "load":{"cca-util-percentage":42,"stations":7}}]}
        """;

    private static IReadOnlyList<JsonElement> Rows(string json) => WlcYang.Rows(json);

    private static IReadOnlyList<WlcClientRow> Clients() => WlcCatalog.ParseClients(
        Rows(CommonJson), Rows(Dot11Json), Rows(TrafficJson), Rows(SisfJson));

    private static IReadOnlyList<WlcApRow> Aps() => WlcCatalog.ParseAps(
        Rows(CapwapJson), Rows(RadioJson), Rows(ApTagJson), Rows(JoinJson));

    // ===== 封筒 =====

    [Fact]
    public void The_envelope_is_unwrapped_without_matching_the_module_name()
    {
        // モジュール接頭辞は版や augment で変わるので、名前で照合しない
        Assert.Equal(2, Rows(CommonJson).Count);
        Assert.Equal(2, Rows("""{"whatever:anything":[{"a":1},{"a":2}]}""").Count);
    }

    [Fact]
    public void A_single_keyed_get_returns_an_object_and_still_counts_as_one_row()
        => Assert.Single(Rows("""{"mod:list":{"client-mac":"aabb.ccdd.eeff"}}"""));

    [Fact]
    public void No_content_and_broken_json_are_zero_rows_not_an_error()
    {
        // 204 No Content は「0 件」であって失敗ではない
        Assert.Empty(Rows(""));
        Assert.Empty(Rows("   "));
        Assert.Empty(Rows("{ broken"));
        Assert.Empty(Rows("""{"mod:list":[]}"""));
    }

    // ===== リーフの候補 =====

    [Fact]
    public void Leaf_candidates_are_tried_in_order_and_nested_paths_work()
    {
        JsonElement node = Rows(JoinJson)[1];

        // 1 台目は素のリーフ、2 台目は入れ子。同じ呼び方で両方拾える
        Assert.Equal("AP-3F-09", WlcYang.First(node, "ap-name", "ap-join-info/ap-name"));
        Assert.Equal("Heartbeat timeout",
            WlcYang.First(node, "last-disconnect-reason", "ap-disconnect-detail/disconnect-reason-str"));

        // どれにも当たらなければ空文字。例外にしない
        Assert.Equal("", WlcYang.First(node, "no-such-leaf", "another/missing/one"));
    }

    [Fact]
    public void Numbers_are_read_whether_they_come_as_number_or_text()
    {
        JsonElement asNumber = Rows(TrafficJson)[0];
        JsonElement asText = Rows(TrafficJson)[1];

        Assert.Equal(-58, WlcYang.Int(asNumber, "most-recent-rssi"));
        Assert.Equal(-78, WlcYang.Int(asText, "most-recent-rssi"));
        Assert.Null(WlcYang.Int(asNumber, "no-such-leaf"));
    }

    // ===== クライアント =====

    [Fact]
    public void Client_lists_are_joined_by_mac_and_the_ip_comes_from_the_binding_table()
    {
        IReadOnlyList<WlcClientRow> rows = Clients();

        Assert.Equal(2, rows.Count);
        Assert.Equal("AP-1F-01", rows[0].ApName);
        Assert.Equal("Corp", rows[0].Ssid);
        Assert.Equal("5GHz ch36", rows[0].Radio);
        Assert.Equal(-58, rows[0].Rssi);
        Assert.Equal("良い", rows[0].Quality);
        Assert.Equal("● 通信中", rows[0].State);

        // IP は sisf にしか無い
        Assert.Equal("192.168.10.50", rows[0].Ip);

        // 載っていない端末は空。0.0.0.0 などで埋めない
        Assert.Equal("", rows[1].Ip);
        Assert.Equal("2.4GHz ch6", rows[1].Radio);
        Assert.Equal(SeverityKind.Notice, rows[1].StateKind);
    }

    [Theory]
    [InlineData("192.168.10.50")]        // IP で
    [InlineData("aabb.ccdd.eeff")]       // Cisco 表記
    [InlineData("AA:BB:CC:DD:EE:FF")]    // コロン区切り・大文字
    [InlineData("aa-bb-cc-dd-ee-ff")]    // ハイフン区切り
    [InlineData("AP-1F-01")]             // AP の名前
    [InlineData("Corp")]                 // SSID
    public void A_client_can_be_found_however_the_query_was_typed(string query)
    {
        WlcClientRow row = Assert.Single(WlcCatalog.FilterClients(Clients(), query));

        Assert.Equal("aabb.ccdd.eeff", row.Mac);
    }

    [Fact]
    public void An_empty_query_keeps_every_client()
        => Assert.Equal(2, WlcCatalog.FilterClients(Clients(), "  ").Count);

    // ===== AP =====

    [Fact]
    public void An_ap_is_matched_even_though_config_uses_the_ethernet_mac()
    {
        IReadOnlyList<WlcApRow> rows = Aps();

        // ap-tag は Ethernet MAC(…22ff)、capwap は無線 MAC(…2200)。
        // 素直に突き合わせると全 AP が「未接続」になる
        WlcApRow joined = Assert.Single(rows, r => r.Name == "AP-1F-01");

        Assert.True(joined.IsJoined);
        Assert.Equal("● 接続中", joined.State);
        Assert.Equal("C9130AXI-Q", joined.Model);
        Assert.Equal("17.9.4", joined.Version);
        Assert.Equal("PT-Office", joined.Tags);
        Assert.Equal("2.4GHz ch1 ● / 5GHz ch36 ●", joined.Radios);
    }

    [Fact]
    public void An_ap_that_is_configured_but_not_joined_is_listed_as_disconnected()
    {
        WlcApRow missing = Assert.Single(Aps(), r => !r.IsJoined);

        Assert.Equal("AP-3F-09", missing.Name);
        Assert.Equal("✕ 未接続", missing.State);
        Assert.Equal(SeverityKind.Alert, missing.StateKind);

        // 繋がっていないので、これらは分からない。埋めない
        Assert.Equal("", missing.Ip);
        Assert.Equal("—", missing.ClientsText);
    }

    [Fact]
    public void An_ap_only_known_from_join_stats_is_still_listed()
    {
        // タグを当てていない AP は ap-cfg に出ない。参加記録との和を見る
        IReadOnlyList<WlcApRow> rows = WlcCatalog.ParseAps(
            Rows(CapwapJson), Rows(RadioJson), [], Rows(JoinJson));

        Assert.Contains(rows, r => r.Name == "AP-3F-09" && !r.IsJoined);
    }

    [Fact]
    public void The_same_ap_is_not_listed_twice()
    {
        // ap-tag と ap-join-stats の両方に居る AP は 1 行だけ
        Assert.Equal(2, Aps().Count);
    }

    [Fact]
    public void Client_counts_are_taken_from_the_client_list()
    {
        IReadOnlyDictionary<string, int> counts = WlcCatalog.CountClientsByAp(Clients());

        IReadOnlyList<WlcApRow> rows = WlcCatalog.ParseAps(
            Rows(CapwapJson), Rows(RadioJson), Rows(ApTagJson), Rows(JoinJson), counts);

        Assert.Equal("1", Assert.Single(rows, r => r.IsJoined).ClientsText);
    }

    // ===== 参加・切断 =====

    [Fact]
    public void Join_stats_read_both_flat_and_nested_leaf_names()
    {
        IReadOnlyList<WlcJoinRow> rows = WlcCatalog.ParseJoins(Rows(JoinJson), Aps());

        Assert.Equal("2026-08-15T08:00:00+09:00", rows[0].LastJoin);
        Assert.Equal("3", rows[0].Joins);
        Assert.Equal("● 接続中", rows[0].State);

        Assert.Equal("AP-3F-09", rows[1].Name);
        Assert.Equal("2026-08-16T21:15:00+09:00", rows[1].LastDisconnect);
        Assert.Equal("Heartbeat timeout", rows[1].Reason);
        Assert.Equal("✕ 未接続", rows[1].State);

        // 無い値は空文字（0 と混同させない）
        Assert.Equal("", rows[1].Joins);
    }

    // ===== 帯域とチャンネル =====

    [Theory]
    [InlineData("dot11-2-4-ghz", "", "6", "2.4GHz ch6")]
    [InlineData("dot11-5-ghz", "", "36", "5GHz ch36")]
    [InlineData("dot11-6-ghz", "", "37", "6GHz ch37")]
    [InlineData("", "0", "11", "2.4GHz ch11")]       // 種別が無ければスロットで
    [InlineData("", "2", "69", "6GHz ch69")]
    [InlineData("", "", "6", "2.4GHz ch6")]          // 最後はチャンネル番号で
    [InlineData("", "", "149", "5GHz ch149")]
    [InlineData("", "", "", "")]                     // 分からなければ捏造しない
    public void The_band_is_worked_out_from_whichever_field_is_present(
        string radioType, string slot, string channel, string expected)
        => Assert.Equal(expected, WlcCatalog.DescribeRadio(radioType, slot, channel));

    // ===== 電波の混み具合 =====

    [Theory]
    [InlineData(10, SeverityKind.Ok)]
    [InlineData(55, SeverityKind.Notice)]
    [InlineData(85, SeverityKind.Alert)]
    [InlineData(-1, SeverityKind.Muted)]
    public void Channel_utilisation_changes_colour_where_it_starts_to_hurt(int percent, SeverityKind expected)
        => Assert.Equal(expected, WlcCatalog.DescribeUtilization(percent).Kind);

    // ===== 不正 AP =====

    [Fact]
    public void Rogues_and_neighbours_share_one_table_with_a_kind_column()
    {
        const string rogueJson = """
            {"mod:rogue-data":[{"rogue-address":"0011.2233.4455","rogue-ssid":"free-wifi",
              "rogue-class-type":"malicious","rogue-radio":{"channel":11,"rssi":-70}}]}
            """;

        const string neighborJson = """
            {"mod:ap-radio-neighbor":[{"bssid":"0066.7788.99aa","ssid":"NextDoor",
              "channel":40,"rssi":-82,"ap-name":"AP-1F-01"}]}
            """;

        IReadOnlyList<WlcRogueRow> rows = WlcCatalog.ParseRogues(Rows(rogueJson), Rows(neighborJson));

        Assert.Equal("不正", rows[0].Kind);
        Assert.Equal("悪意あり", rows[0].Note);
        Assert.Equal(-70, rows[0].Rssi);

        Assert.Equal("隣接", rows[1].Kind);
        Assert.Equal("AP-1F-01", rows[1].DetectedBy);
    }

    [Fact]
    public void An_unknown_classification_is_shown_as_it_came()
        => Assert.Equal("brand-new", WlcCatalog.DescribeRogueClass("brand-new"));

    // ===== SSID =====

    [Fact]
    public void Each_ssid_shows_how_many_are_on_which_band()
    {
        const string wlanJson = """
            {"mod:wlan-cfg-entry":[
              {"profile-name":"Corp-Profile","wlan-id":1,
               "apf-vap-id-data":{"ssid":"Corp","wlan-status":true}},
              {"profile-name":"Guest-Profile","wlan-id":2,
               "apf-vap-id-data":{"ssid":"Guest","wlan-status":false}}]}
            """;

        IReadOnlyList<WlcSsidRow> rows = WlcCatalog.ParseSsids(Rows(wlanJson), Clients());

        Assert.Equal("● 有効", rows[0].State);
        Assert.Equal(1, rows[0].Clients);
        Assert.Equal(1, rows[0].Band5);
        Assert.Equal(0, rows[0].Band24);

        Assert.Equal("✕ 無効", rows[1].State);
        Assert.Equal(1, rows[1].Band24);
    }

    // ===== 失敗の文言 =====

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(406)]
    [InlineData(503)]
    public void Failures_are_described_in_japanese_with_the_code(int status)
        => Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture),
                           WlcCatalog.DescribeFailure(status), StringComparison.Ordinal);

    [Fact]
    public void A_404_points_at_the_ssh_fallback()
        => Assert.Contains("SSH", WlcCatalog.DescribeFailure(404), StringComparison.Ordinal);

    // ===== CSV =====

    [Fact]
    public void Every_table_keeps_one_column_per_header()
    {
        IReadOnlyList<WlcClientRow> clients = Clients();
        IReadOnlyList<WlcApRow> aps = Aps();

        CsvTable[] tables =
        [
            WlcCatalog.ToCsv(clients),
            WlcCatalog.ToCsv(aps),
            WlcCatalog.ToCsv(WlcCatalog.ParseJoins(Rows(JoinJson), aps)),
            WlcCatalog.ToCsv(WlcCatalog.ParseRrm(Rows(RrmJson), aps)),
            WlcCatalog.ToCsv(WlcCatalog.ParseRogues(
                Rows("""{"m:rogue-data":[{"rogue-address":"0011.2233.4455"}]}"""), [])),
            WlcCatalog.ToCsv(WlcCatalog.ParseSsids(
                Rows("""{"m:wlan-cfg-entry":[{"profile-name":"P","apf-vap-id-data":{"ssid":"Corp"}}]}"""), clients)),
        ];

        foreach (CsvTable table in tables)
        {
            Assert.NotEmpty(table.Rows);
            Assert.All(table.Rows, row => Assert.Equal(table.Headers.Count, row.Length));
        }
    }

    [Fact]
    public void Rrm_rows_get_the_ap_name_from_the_ap_list()
    {
        IReadOnlyList<WlcRrmRow> rows = WlcCatalog.ParseRrm(Rows(RrmJson), Aps());

        Assert.Equal("AP-1F-01", rows[0].ApName);
        Assert.Equal("5GHz", rows[0].Radio);
        Assert.Equal("42%", rows[0].UtilizationText);
        Assert.Equal("7", rows[0].ClientsText);
    }
}
