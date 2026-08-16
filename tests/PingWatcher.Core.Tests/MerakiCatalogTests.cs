using PingWatcher.Core.Cloud;
using PingWatcher.Core.Net;
using PingWatcher.Core.Work;
using Xunit;

namespace PingWatcher.Core.Tests;

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
}
