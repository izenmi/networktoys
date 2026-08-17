using NetworkToys.Core.Cloud;
using NetworkToys.Core.Design;
using NetworkToys.Core.Net;
using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

public class MerakiCatalogTests
{
    // ===== サンプル（実際の応答から必要な項目だけ抜いたもの） =====

    private const string NetworksJson = """
        [
          { "id": "N_111", "name": "本社", "productTypes": ["appliance", "switch"],
            "timeZone": "Asia/Tokyo", "tags": ["hq", "jp"] },
          { "id": "N_222", "name": "大阪支店", "productTypes": ["appliance"],
            "timeZone": "Asia/Tokyo", "tags": [] }
        ]
        """;

    private const string DevicesJson = """
        [
          { "serial": "Q2AA-1111-AAAA", "name": "本社MX", "model": "MX68",
            "firmware": "wired-18-107", "networkId": "N_111", "mac": "00:11:22:33:44:55" },
          { "serial": "Q2BB-2222-BBBB", "name": "大阪MX", "model": "MX67",
            "firmware": "wired-18-107", "networkId": "N_222" }
        ]
        """;

    private const string DeviceStatusesJson = """
        [
          { "serial": "Q2AA-1111-AAAA", "status": "online",
            "publicIp": "203.0.113.5", "lanIp": "192.168.10.1", "networkId": "N_111" },
          { "serial": "Q2CC-3333-CCCC", "status": "offline", "name": "撤去予定AP",
            "model": "MR33", "networkId": "N_111" }
        ]
        """;

    private const string UplinksJson = """
        [
          { "networkId": "N_111", "serial": "Q2AA-1111-AAAA", "model": "MX68",
            "uplinks": [
              { "interface": "wan1", "status": "active", "ip": "198.51.100.2",
                "gateway": "198.51.100.1", "publicIp": "203.0.113.5" },
              { "interface": "wan2", "status": "ready", "ip": "192.0.2.2",
                "gateway": "192.0.2.1", "publicIp": "203.0.113.9" }
            ] }
        ]
        """;

    private const string ClientsJson = """
        [
          { "id": "k1", "description": "PC-001", "ip": "192.168.10.20",
            "mac": "aa:bb:cc:dd:ee:ff", "vlan": 10, "manufacturer": "Intel",
            "usage": { "sent": 1024.0, "recv": 3072.0 }, "lastSeen": "2026-08-16T01:02:03Z" },
          { "id": "k2", "dhcpHostname": "PRINTER", "ip": "192.168.10.30",
            "mac": "11:22:33:44:55:66", "vlan": "20", "manufacturer": "Canon" }
        ]
        """;

    // ===== ネットワーク =====

    [Fact]
    public void Networks_flatten_their_arrays_into_readable_text()
    {
        IReadOnlyList<MerakiNetworkRow> rows = MerakiCatalog.ParseNetworks([NetworksJson]);

        Assert.Equal(2, rows.Count);
        Assert.Equal("本社", rows[0].Name);
        Assert.Equal("appliance, switch", rows[0].ProductTypes);
        Assert.Equal("hq, jp", rows[0].Tags);
        // 空配列は空文字。"[]" のような生の表記を出さない
        Assert.Equal("", rows[1].Tags);
    }

    // ===== 機器（突き合わせ） =====

    [Fact]
    public void Devices_and_statuses_are_joined_by_serial()
    {
        IReadOnlyList<MerakiNetworkRow> networks = MerakiCatalog.ParseNetworks([NetworksJson]);
        IReadOnlyList<MerakiDeviceRow> rows =
            MerakiCatalog.JoinDevices([DevicesJson], [DeviceStatusesJson], networks);

        MerakiDeviceRow hq = rows.Single(r => r.Serial == "Q2AA-1111-AAAA");

        // 型番とファームは機器一覧から、状態とグローバル IP は稼働状況から来る
        Assert.Equal("MX68", hq.Model);
        Assert.Equal("wired-18-107", hq.Firmware);
        Assert.Equal("203.0.113.5", hq.PublicIp);
        Assert.Equal("192.168.10.1", hq.LanIp);
        // networkId はネットワーク名に解決される
        Assert.Equal("本社", hq.Network);
    }

    [Fact]
    public void Devices_missing_from_either_side_are_still_listed()
    {
        IReadOnlyList<MerakiNetworkRow> networks = MerakiCatalog.ParseNetworks([NetworksJson]);
        IReadOnlyList<MerakiDeviceRow> rows =
            MerakiCatalog.JoinDevices([DevicesJson], [DeviceStatusesJson], networks);

        // 機器一覧 2 台 + 稼働状況にしかいない 1 台
        Assert.Equal(3, rows.Count);

        // 稼働状況が無い機器も消えない（状態は「—」になるだけ）
        MerakiDeviceRow osaka = rows.Single(r => r.Serial == "Q2BB-2222-BBBB");
        Assert.Equal("—", osaka.State);
        Assert.Equal("", osaka.PublicIp);

        // 機器一覧に無い機器も消えない
        MerakiDeviceRow ghost = rows.Single(r => r.Serial == "Q2CC-3333-CCCC");
        Assert.Equal("撤去予定AP", ghost.Name);
        Assert.Equal("✕ 停止", ghost.State);
    }

    [Fact]
    public void Unknown_network_ids_fall_back_to_the_raw_id()
    {
        IReadOnlyList<MerakiDeviceRow> rows =
            MerakiCatalog.JoinDevices([DevicesJson], [DeviceStatusesJson], []);

        // 名前が引けないときに空欄にすると、どのネットワークの機器か分からなくなる
        Assert.Equal("N_111", rows.Single(r => r.Serial == "Q2AA-1111-AAAA").Network);
    }

    // ===== アップリンク =====

    [Fact]
    public void Each_uplink_becomes_its_own_row()
    {
        IReadOnlyList<MerakiNetworkRow> networks = MerakiCatalog.ParseNetworks([NetworksJson]);
        IReadOnlyList<MerakiUplinkRow> rows = MerakiCatalog.ParseUplinks([UplinksJson], networks);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "wan1", "wan2" }, rows.Select(r => r.Interface).ToArray());
        Assert.Equal("本社", rows[0].Network);
        Assert.Equal("● 稼働", rows[0].State);
        Assert.Equal(ConnectionStateKind.Ok, rows[0].StateKind);
        Assert.Equal("◌ 待機", rows[1].State);
        Assert.Equal("198.51.100.1", rows[0].Gateway);
    }

    [Fact]
    public void Global_ip_summary_folds_duplicates_and_handles_empty()
    {
        IReadOnlyList<MerakiUplinkRow> rows = MerakiCatalog.ParseUplinks([UplinksJson], []);

        Assert.Equal("203.0.113.5 / 203.0.113.9", MerakiCatalog.GlobalIpSummary(rows));
        Assert.Equal("—", MerakiCatalog.GlobalIpSummary([]));
    }

    // ===== 状態の表示 =====

    [Theory]
    [InlineData("online", "● 稼働")]
    [InlineData("alerting", "⊘ 警報")]
    [InlineData("dormant", "◌ 休止")]
    [InlineData("offline", "✕ 停止")]
    public void Device_states_get_a_symbol_and_a_word(string status, string expected)
        => Assert.Equal(expected, MerakiCatalog.DescribeDeviceStatus(status).Text);

    [Fact]
    public void Unknown_states_are_shown_as_they_came()
    {
        // 知らない値を勝手に「停止」などと言い換えない
        Assert.Equal("rebooting", MerakiCatalog.DescribeDeviceStatus("rebooting").Text);
        Assert.Equal("degraded", MerakiCatalog.DescribeUplinkStatus("degraded").Text);
        Assert.Equal("—", MerakiCatalog.DescribeUplinkStatus(null).Text);
    }

    // ===== クライアント =====

    [Fact]
    public void Clients_accept_vlan_as_number_or_text()
    {
        IReadOnlyList<MerakiClientRow> rows = MerakiCatalog.ParseClients([ClientsJson]);

        Assert.Equal(2, rows.Count);
        // 数値でも文字列でも同じ見え方になる（型を決め打つと一覧ごと落ちる）
        Assert.Equal("10", rows[0].Vlan);
        Assert.Equal("20", rows[1].Vlan);
    }

    [Fact]
    public void Client_usage_is_summed_from_kilobytes()
    {
        IReadOnlyList<MerakiClientRow> rows = MerakiCatalog.ParseClients([ClientsJson]);

        // 1024 + 3072 KB = 4096 KB = 4.0 MB
        Assert.Equal("4.0 MB", rows[0].Usage);
        // usage が無いクライアントもある
        Assert.Equal("—", rows[1].Usage);
    }

    [Fact]
    public void Clients_without_a_description_fall_back_to_the_dhcp_name()
    {
        IReadOnlyList<MerakiClientRow> rows = MerakiCatalog.ParseClients([ClientsJson]);

        Assert.Equal("PC-001", rows[0].Description);
        Assert.Equal("PRINTER", rows[1].Description);
    }

    // ===== 応答の読み取り =====

    [Fact]
    public void Pages_are_concatenated_and_broken_pages_are_skipped()
    {
        IReadOnlyList<MerakiNetworkRow> rows =
            MerakiCatalog.ParseNetworks([NetworksJson, "こわれています", "[]", NetworksJson]);

        // 壊れたページで全体を落とさない
        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public void Next_page_url_is_taken_from_the_link_header()
    {
        const string header =
            "<https://api.meraki.com/api/v1/organizations/1/devices?startingAfter=A>; rel=first, " +
            "<https://api.meraki.com/api/v1/organizations/1/devices?startingAfter=B>; rel=next";

        Assert.Equal(
            "https://api.meraki.com/api/v1/organizations/1/devices?startingAfter=B",
            MerakiCatalog.NextPageUrl(header));
    }

    [Fact]
    public void Quoted_rel_values_are_also_understood()
        => Assert.Equal("https://example.test/p2", MerakiCatalog.NextPageUrl("<https://example.test/p2>; rel=\"next\""));

    [Fact]
    public void The_last_page_has_no_next_link()
    {
        Assert.Null(MerakiCatalog.NextPageUrl("<https://example.test/p1>; rel=first, <https://example.test/p1>; rel=prev"));
        Assert.Null(MerakiCatalog.NextPageUrl(null));
        Assert.Null(MerakiCatalog.NextPageUrl(""));
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("0", 1)]        // 0 秒は待たない扱いにせず 1 秒に寄せる
    [InlineData("9999", 60)]    // 長すぎる指定で画面が止まらないようにする
    [InlineData("Wed, 21 Oct 2026 07:28:00 GMT", 1)]   // 日付形式は読まない
    [InlineData(null, 1)]
    public void Retry_after_is_clamped_to_a_sane_range(string? header, int expected)
        => Assert.Equal(expected, MerakiCatalog.RetryAfterSeconds(header));

    [Fact]
    public void Failures_are_described_in_japanese()
    {
        Assert.Contains("API キー", MerakiCatalog.DescribeFailure(401));
        Assert.Contains("429", MerakiCatalog.DescribeFailure(429));
        Assert.Contains("503", MerakiCatalog.DescribeFailure(503));
        Assert.Contains("418", MerakiCatalog.DescribeFailure(418));
    }

    // ===== CSV =====

    [Fact]
    public void Csv_keeps_one_column_per_header()
    {
        IReadOnlyList<MerakiNetworkRow> networks = MerakiCatalog.ParseNetworks([NetworksJson]);
        CsvTable table = MerakiCatalog.ToCsv(networks);

        Assert.Equal(5, table.Headers.Count);
        Assert.All(table.Rows, row => Assert.Equal(table.Headers.Count, row.Length));
    }

    [Fact]
    public void Csv_neutralises_values_that_look_like_formulas()
    {
        const string json = """[ { "id": "N_1", "name": "=cmd|'/c calc'!A1" } ]""";

        string csv = MerakiCatalog.ToCsv(MerakiCatalog.ParseNetworks([json])).ToCsv();

        // 既存の CSV 出力と同じ無害化（先頭に ' を足す）を通っていること
        Assert.Contains("'=cmd", csv);
    }

    // ===== 拠点・DHCP・アラート =====

    [Fact]
    public void Only_segments_that_actually_have_clients_are_kept()
    {
        const string routes = """
            [ {"subnet":"192.168.10.0/24","name":"1F","enabled":true},
              {"subnet":"192.168.20.0/24","name":"2F","enabled":true},
              {"subnet":"10.9.9.0/24","name":"止めた経路","enabled":false} ]
            """;

        IReadOnlyList<string> subnets = MerakiCatalog.ParseStaticRouteSubnets([routes]);

        // 無効な経路は最初から出さない
        Assert.Equal(2, subnets.Count);

        IReadOnlyList<string> kept = MerakiCatalog.SegmentsWithClients(
            subnets, ["192.168.10.55", "203.0.113.9"]);

        // 誰も居ないセグメントは落とす（設定だけ残っている経路を並べても読み違えるだけ）
        Assert.Equal(["192.168.10.0/24"], kept);
    }

    [Fact]
    public void Segments_are_added_to_the_appliance_only()
    {
        MerakiDeviceRow appliance = new(
            "MX-1F", "MX68", "Q2AA-1111-AAAA", "18.1", "本社", "● 稼働",
            ConnectionStateKind.Ok, "203.0.113.5", "192.168.1.1");

        MerakiDeviceRow switchRow = appliance with { Name = "SW-1F", Model = "MS120", LanIp = "192.168.1.2" };

        IReadOnlyList<MerakiDeviceRow> rows = MerakiCatalog.WithSegments(
            [appliance, switchRow],
            new Dictionary<string, string> { ["本社"] = "192.168.10.0/24" });

        Assert.Equal("192.168.1.1 / 192.168.10.0/24", rows[0].LanIp);

        // スイッチや AP に経路の話を混ぜない
        Assert.Equal("192.168.1.2", rows[1].LanIp);
    }

    [Fact]
    public void Dhcp_usage_is_worked_out_from_used_and_free()
    {
        const string json = """
            [ {"vlanId":10,"subnet":"192.168.10.0/24","usedCount":180,"freeCount":20},
              {"vlanId":20,"subnet":"192.168.20.0/24","usedCount":5,"freeCount":245} ]
            """;

        IReadOnlyList<MerakiDhcpRow> rows = MerakiCatalog.ParseDhcp([json], "本社", "MX-1F");

        Assert.Equal("10", rows[0].Vlan);
        Assert.Equal("90%", rows[0].UsageText);
        Assert.Equal(SeverityKind.Alert, rows[0].UsageKind);

        Assert.Equal("2%", rows[1].UsageText);
        Assert.Equal(SeverityKind.Ok, rows[1].UsageKind);
    }

    [Theory]
    [InlineData(95, SeverityKind.Alert)]
    [InlineData(75, SeverityKind.Notice)]
    [InlineData(10, SeverityKind.Ok)]
    [InlineData(-1, SeverityKind.Muted)]
    public void Dhcp_usage_changes_colour_before_it_runs_out(int percent, SeverityKind expected)
        => Assert.Equal(expected, MerakiCatalog.DescribeDhcpUsage(percent).Kind);

    [Fact]
    public void Alerts_read_the_nested_network_and_device_names()
    {
        const string json = """
            [ {"severity":"critical","categoryType":"connectivity","startedAt":"2026-08-17T01:00:00Z",
               "title":"Uplink down","network":{"name":"本社"},"device":{"name":"MX-1F"}},
              {"severity":"brand-new","title":"何か","network":{"name":"支社"}} ]
            """;

        IReadOnlyList<MerakiAlertRow> rows = MerakiCatalog.ParseAlerts([json]);

        Assert.Equal("✕ 重大", rows[0].Severity);
        Assert.Equal("本社", rows[0].Network);
        Assert.Equal("MX-1F", rows[0].Device);
        Assert.Equal("Uplink down", rows[0].Detail);

        // 知らない重大度はそのまま出す
        Assert.Equal("brand-new", rows[1].Severity);
        Assert.Equal("", rows[1].Device);
    }

    [Fact]
    public void The_new_tables_keep_one_column_per_header()
    {
        CsvTable[] tables =
        [
            MerakiCatalog.ToCsv([MerakiCatalog.SiteRow(
                new MerakiNetworkRow("本社", "N_1", "appliance", "Asia/Tokyo", ""),
                12, ["192.168.10.0/24"], "")]),
            MerakiCatalog.ToCsv(MerakiCatalog.ParseDhcp(
                ["""[{"vlanId":10,"subnet":"192.168.10.0/24","usedCount":1,"freeCount":9}]"""], "本社", "MX-1F")),
            MerakiCatalog.ToCsv(MerakiCatalog.ParseAlerts(
                ["""[{"severity":"warning","title":"x"}]"""])),
        ];

        foreach (CsvTable table in tables)
        {
            Assert.NotEmpty(table.Rows);
            Assert.All(table.Rows, row => Assert.Equal(table.Headers.Count, row.Length));
        }
    }

    [Fact]
    public void Device_utilisation_reads_the_nested_average_percentage()
    {
        const string json = """
            [ {"name":"MX-1F","model":"MX68","network":{"name":"本社"},
               "utilization":{"average":{"percentage":12.5}}},
              {"name":"MX-2F","model":"MX68","network":{"name":"支社"}} ]
            """;

        IReadOnlyList<MerakiUtilizationRow> rows = MerakiCatalog.ParseUtilization([json]);

        Assert.Equal("12.5%", rows[0].ValueText);
        Assert.Equal("本社", rows[0].Network);

        // 入れ子が無くても落とさない（0 として出す）
        Assert.Equal(0, rows[1].Value);
    }

    [Fact]
    public void Device_usage_is_read_in_kilobytes_and_shown_in_units()
    {
        const string json = """
            [ {"name":"AP-1","model":"MR46","network":{"name":"本社"},"usage":{"total":2097152}} ]
            """;

        IReadOnlyList<MerakiUtilizationRow> rows = MerakiCatalog.ParseUsage([json]);

        Assert.Equal("2.0 GB", rows[0].ValueText);
    }

    [Theory]
    [InlineData(0, "—")]
    [InlineData(512, "512 KB")]
    [InlineData(2048, "2.0 MB")]
    public void Kilobytes_are_described_in_readable_units(double kilobytes, string expected)
        => Assert.Equal(expected, MerakiCatalog.DescribeKilobytes(kilobytes));
}
