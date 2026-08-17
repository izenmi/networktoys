using NetworkToys.Core.Cloud;
using NetworkToys.Core.Net;
using NetworkToys.Core.Verify;
using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// 導入時確認の判定。<b>合否そのものが機能</b>なので、
/// 合格・不合格・取れなかった場合の 3 通りを項目ごとに固定する。
/// </summary>
public class MerakiInstallCheckTests
{
    // ===== 機器の稼働 =====

    [Fact]
    public void Devices_pass_when_every_device_is_online()
    {
        MerakiCheckRow row = MerakiInstallCheck.Devices(
            [Device("MX-1F", "online"), Device("MS-2F", "online")]);

        Assert.Equal(CheckVerdict.Pass, row.Verdict);
        Assert.Equal("○ 合格", row.VerdictText);
    }

    [Fact]
    public void Devices_fail_and_name_the_ones_that_are_not_online()
    {
        MerakiCheckRow row = MerakiInstallCheck.Devices(
            [Device("MX-1F", "online"), Device("MS-2F", "offline"), Device("MR-3F", "alerting")]);

        Assert.Equal(CheckVerdict.Fail, row.Verdict);
        Assert.Contains("MS-2F", row.Detail, StringComparison.Ordinal);
        Assert.Contains("MR-3F", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Devices_are_skipped_when_the_list_is_empty()
        => Assert.Equal(CheckVerdict.Skipped, MerakiInstallCheck.Devices([]).Verdict);

    // ===== インターネット回線（WAN） =====

    private const string UplinkSettingsJson = """
        { "interfaces": {
            "wan1": { "enabled": true,  "svis": { "ipv4": { "assignmentMode": "static" } } },
            "wan2": { "enabled": false, "svis": { "ipv4": { "assignmentMode": "dynamic" } } } } }
        """;

    [Fact]
    public void Wan_ignores_the_uplink_that_is_not_enabled()
    {
        // wan2 は使わない設定なので、未接続でも不合格にしない
        MerakiCheckRow row = MerakiInstallCheck.Wan(
            "MX-1F", [UplinkSettingsJson], [Uplink("wan1", "active"), Uplink("wan2", "not connected")]);

        Assert.Equal(CheckVerdict.Pass, row.Verdict);
    }

    [Fact]
    public void Wan_fails_when_an_enabled_uplink_is_down()
    {
        const string json = """
            { "interfaces": { "wan1": { "enabled": true }, "wan2": { "enabled": true } } }
            """;

        MerakiCheckRow row = MerakiInstallCheck.Wan(
            "MX-1F", [json], [Uplink("wan1", "active"), Uplink("wan2", "not connected")]);

        Assert.Equal(CheckVerdict.Fail, row.Verdict);
        Assert.Contains("wan2", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Wan_warns_when_the_settings_could_not_be_read()
    {
        // 設定が取れないときは状態だけで見る。黙って合格にはしない
        MerakiCheckRow row = MerakiInstallCheck.Wan("MX-1F", [], [Uplink("wan1", "active")]);

        Assert.Equal(CheckVerdict.Warn, row.Verdict);
    }

    [Fact]
    public void Wan_fails_when_nothing_is_up_at_all()
        => Assert.Equal(
            CheckVerdict.Fail,
            MerakiInstallCheck.Wan("MX-1F", [], [Uplink("wan1", "not connected")]).Verdict);

    [Theory]
    [InlineData("active", true)]
    [InlineData("ready", true)]      // 冗長側で待機しているだけ＝上がっている
    [InlineData("connecting", false)] // まだ上がっていない
    [InlineData("not connected", false)]
    [InlineData("failed", false)]
    public void Ready_counts_as_linked_up_but_connecting_does_not(string status, bool up)
        => Assert.Equal(up, MerakiInstallCheck.IsLinkedUp(Uplink("wan1", status)));

    [Fact]
    public void Enabled_uplinks_are_null_when_the_answer_is_unreadable()
    {
        Assert.Null(MerakiInstallCheck.ParseEnabledUplinks([]));
        Assert.Null(MerakiInstallCheck.ParseEnabledUplinks(["これは JSON ではない"]));

        // 読めたが 1 本も有効でない、は null と意味が違う
        Assert.Empty(MerakiInstallCheck.ParseEnabledUplinks(
            ["""{"interfaces":{"wan1":{"enabled":false}}}"""])!);
    }

    // ===== 回線の品質 =====

    // 値は JSON に埋めるので文字で渡す（数で渡すと環境の小数点で「2,0」になりうる）
    [Theory]
    [InlineData("0", "10", CheckVerdict.Pass)]
    [InlineData("2.0", "10", CheckVerdict.Warn)]   // ロスが目安を超えた
    [InlineData("0", "300", CheckVerdict.Warn)]    // 遅延が目安を超えた
    [InlineData("9.0", "10", CheckVerdict.Fail)]
    public void Quality_is_judged_from_loss_and_latency(string loss, string latency, CheckVerdict expected)
    {
        string json = $$"""
            [ { "networkId":"N_1", "serial":"Q2XX-1111-1111", "uplink":"wan1", "ip":"8.8.8.8",
                "timeSeries":[ {"lossPercent":{{loss}},"latencyMs":{{latency}}} ] } ]
            """;

        IReadOnlyList<MerakiCheckRow> rows = MerakiInstallCheck.Quality([json], [Appliance()]);

        Assert.Equal(expected, Assert.Single(rows).Verdict);
    }

    [Fact]
    public void Quality_is_skipped_when_the_values_are_still_empty()
    {
        // 上がった直後は値が null で返る。0% と混同させない
        const string json = """
            [ { "serial":"Q2XX-1111-1111", "uplink":"wan1",
                "timeSeries":[ {"lossPercent":null,"latencyMs":null} ] } ]
            """;

        Assert.Equal(CheckVerdict.Skipped, MerakiInstallCheck.Quality([json], [Appliance()])[0].Verdict);
    }

    [Fact]
    public void Quality_only_looks_at_the_appliances_of_this_site()
    {
        const string json = """
            [ { "serial":"Q2YY-9999-9999", "uplink":"wan1",
                "timeSeries":[ {"lossPercent":0,"latencyMs":5} ] } ]
            """;

        // ほかの拠点の回線しか入っていない＝この拠点の実測値は無い
        Assert.Equal(CheckVerdict.Skipped, MerakiInstallCheck.Quality([json], [Appliance()])[0].Verdict);
    }

    // ===== ポートの速度・全二重 =====

    private const string SwitchPortsJson = """
        [ {"portId":"1","enabled":true,"status":"Connected","speed":"1 Gbps","duplex":"full",
           "errors":[],"warnings":[]},
          {"portId":"2","enabled":true,"status":"Disconnected","speed":"","duplex":""},
          {"portId":"3","enabled":true,"status":"Connected","speed":"1000 Mbps","duplex":"full"} ]
        """;

    [Fact]
    public void Ports_pass_when_every_connected_port_is_a_gigabit_and_full()
    {
        MerakiCheckRow row = MerakiInstallCheck.SwitchPorts("MS-2F", [SwitchPortsJson]);

        Assert.Equal(CheckVerdict.Pass, row.Verdict);
        Assert.Contains("2 ポート", row.Detail, StringComparison.Ordinal); // 未接続の 1 本は数えない
    }

    [Fact]
    public void Ports_fail_and_name_the_slow_one()
    {
        const string json = """
            [ {"portId":"1","status":"Connected","speed":"100 Mbps","duplex":"full"},
              {"portId":"2","status":"Connected","speed":"1 Gbps","duplex":"half"} ]
            """;

        MerakiCheckRow row = MerakiInstallCheck.SwitchPorts("MS-2F", [json]);

        Assert.Equal(CheckVerdict.Fail, row.Verdict);
        Assert.Contains("ポート 1", row.Detail, StringComparison.Ordinal);
        Assert.Contains("ポート 2", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Ports_warn_when_the_speed_is_fine_but_errors_are_showing()
    {
        const string json = """
            [ {"portId":"1","status":"Connected","speed":"1 Gbps","duplex":"full",
               "errors":["CRC errors"],"warnings":[]} ]
            """;

        Assert.Equal(CheckVerdict.Warn, MerakiInstallCheck.SwitchPorts("MS-2F", [json]).Verdict);
    }

    [Fact]
    public void Ports_fail_when_nothing_is_plugged_in()
        => Assert.Equal(
            CheckVerdict.Fail,
            MerakiInstallCheck.SwitchPorts("MS-2F", ["""[{"portId":"1","status":"Disconnected"}]"""]).Verdict);

    [Theory]
    [InlineData("1 Gbps", 1000)]
    [InlineData("1Gbps", 1000)]
    [InlineData("1000 Mbps", 1000)]
    [InlineData("100 Mbps", 100)]
    [InlineData("10 Gbps", 10000)]
    [InlineData("2.5 Gbps", 2500)]
    [InlineData("1000", 1000)]
    public void Speed_text_is_turned_into_megabits(string text, double expected)
        => Assert.Equal(expected, MerakiInstallCheck.ParseSpeedMbps(text)!.Value);

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    public void Speed_that_cannot_be_read_is_null(string text)
        => Assert.Null(MerakiInstallCheck.ParseSpeedMbps(text));

    [Fact]
    public void The_appliance_ports_are_handed_to_a_person()
    {
        // ダッシュボード API が持っていないので、行は出すが判定は人がする
        MerakiCheckRow row = MerakiInstallCheck.AppliancePortsByPerson("MX-1F", "MX68");

        Assert.Equal(CheckVerdict.AwaitingPerson, row.Verdict);
        Assert.True(row.NeedsPerson);
        Assert.Equal("◍ 目視で確認", row.VerdictText);
    }

    // ===== VPN =====

    private const string VpnStatusesJson = """
        [ { "networkId":"N_1", "networkName":"本社", "deviceStatus":"online",
            "merakiVpnPeers":[ {"networkId":"N_2","networkName":"支店A","reachability":"reachable"},
                               {"networkId":"N_3","networkName":"支店B","reachability":"unreachable"} ],
            "thirdPartyVpnPeers":[ {"name":"データセンタ","publicIp":"203.0.113.9","reachability":"reachable"} ] } ]
        """;

    [Fact]
    public void Vpn_splits_auto_vpn_from_third_party_peers()
    {
        IReadOnlyList<MerakiCheckRow> rows = MerakiInstallCheck.Vpn([VpnStatusesJson], "N_1");

        Assert.Equal(2, rows.Count);

        Assert.Equal(CheckVerdict.Fail, rows[0].Verdict);
        Assert.Contains("支店B", rows[0].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("支店A", rows[0].Detail, StringComparison.Ordinal);

        Assert.Equal(CheckVerdict.Pass, rows[1].Verdict);
    }

    [Fact]
    public void Vpn_is_skipped_when_the_site_has_no_peers()
    {
        const string json = """
            [ {"networkId":"N_1","merakiVpnPeers":[],"thirdPartyVpnPeers":[]} ]
            """;

        Assert.All(
            MerakiInstallCheck.Vpn([json], "N_1"),
            row => Assert.Equal(CheckVerdict.Skipped, row.Verdict));
    }

    [Fact]
    public void Vpn_is_skipped_when_this_site_is_not_in_the_answer()
        => Assert.Equal(
            CheckVerdict.Skipped,
            Assert.Single(MerakiInstallCheck.Vpn([VpnStatusesJson], "N_9")).Verdict);

    // ===== DHCP =====

    [Fact]
    public void Dhcp_passes_when_addresses_are_actually_handed_out()
    {
        IReadOnlyList<MerakiDhcpRow> subnets = MerakiCatalog.ParseDhcp(
            ["""[{"vlanId":10,"subnet":"192.168.10.0/24","usedCount":12,"freeCount":230}]"""],
            "本社", "MX-1F");

        MerakiCheckRow row = Assert.Single(MerakiInstallCheck.Dhcp(subnets));

        Assert.Equal(CheckVerdict.Pass, row.Verdict);
        Assert.Contains("VLAN 10", row.Target, StringComparison.Ordinal);
    }

    [Fact]
    public void Dhcp_warns_but_does_not_fail_when_nothing_is_leased_yet()
    {
        IReadOnlyList<MerakiDhcpRow> subnets = MerakiCatalog.ParseDhcp(
            ["""[{"vlanId":20,"subnet":"192.168.20.0/24","usedCount":0,"freeCount":250}]"""],
            "本社", "MX-1F");

        Assert.Equal(CheckVerdict.Warn, MerakiInstallCheck.Dhcp(subnets)[0].Verdict);
    }

    [Fact]
    public void Dhcp_warns_when_the_pool_is_nearly_full()
    {
        IReadOnlyList<MerakiDhcpRow> subnets = MerakiCatalog.ParseDhcp(
            ["""[{"vlanId":30,"subnet":"192.168.30.0/24","usedCount":95,"freeCount":5}]"""],
            "本社", "MX-1F");

        Assert.Equal(CheckVerdict.Warn, MerakiInstallCheck.Dhcp(subnets)[0].Verdict);
    }

    [Fact]
    public void Dhcp_is_skipped_when_the_appliance_has_no_subnet()
        => Assert.Equal(CheckVerdict.Skipped, MerakiInstallCheck.Dhcp([])[0].Verdict);

    // ===== クライアント =====

    [Fact]
    public void Clients_fail_when_the_site_is_still_empty()
        => Assert.Equal(CheckVerdict.Fail, MerakiInstallCheck.Clients([], "1 時間").Verdict);

    [Fact]
    public void Clients_pass_and_count_the_ones_with_an_address()
    {
        IReadOnlyList<MerakiClientRow> clients = MerakiCatalog.ParseClients(
            ["""[{"mac":"aa:bb:cc:dd:ee:ff","ip":"192.168.10.5"},{"mac":"11:22:33:44:55:66"}]"""], "本社");

        MerakiCheckRow row = MerakiInstallCheck.Clients(clients, "1 時間");

        Assert.Equal(CheckVerdict.Pass, row.Verdict);
        Assert.Contains("2 台", row.Detail, StringComparison.Ordinal);
        Assert.Contains("1 台", row.Detail, StringComparison.Ordinal);
    }

    // ===== 取れなかった項目 =====

    [Fact]
    public void What_could_not_be_read_keeps_its_reason_and_is_not_called_a_test()
    {
        MerakiCheckRow row = MerakiInstallCheck.Unavailable(MerakiInstallCheck.VpnName, "本社", "HTTP 404");

        Assert.Equal(CheckVerdict.Skipped, row.Verdict);

        // この画面は何も「試験」しないので、試験タブとは言い方を変える
        Assert.Equal("— 確認できず", row.VerdictText);
        Assert.Contains("404", row.Detail, StringComparison.Ordinal);
    }

    // ===== まとめと CSV =====

    [Fact]
    public void The_summary_leads_with_the_failures_and_keeps_the_person_waiting()
    {
        string text = MerakiInstallCheck.Summarize(
        [
            new("A", "x", CheckVerdict.Pass, ""),
            new("B", "x", CheckVerdict.Fail, ""),
            new("C", "x", CheckVerdict.Warn, ""),
            new("D", "x", CheckVerdict.AwaitingPerson, ""),
        ]);

        Assert.StartsWith("✕ 不合格が 1 件あります", text, StringComparison.Ordinal);
        Assert.Contains("目視の 1 件", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_is_empty_before_anything_has_run()
        => Assert.Equal("", MerakiInstallCheck.Summarize([]));

    [Fact]
    public void The_csv_keeps_one_column_per_header()
    {
        CsvTable table = MerakiInstallCheck.ToCsv(
            [new("A", "MX-1F", CheckVerdict.Pass, "よし")]);

        Assert.NotEmpty(table.Rows);
        Assert.All(table.Rows, row => Assert.Equal(table.Headers.Count, row.Length));
    }

    // ===== 小物 =====

    private static MerakiDeviceRow Device(string name, string status)
    {
        (string text, ConnectionStateKind kind) = MerakiCatalog.DescribeDeviceStatus(status);

        return new MerakiDeviceRow(name, "MX68", "Q2XX-1111-1111", "17.10", "本社", text, kind, "", "");
    }

    private static MerakiDeviceRow Appliance() => Device("MX-1F", "online");

    private static MerakiUplinkRow Uplink(string name, string status)
    {
        (string text, ConnectionStateKind kind) = MerakiCatalog.DescribeUplinkStatus(status);

        return new MerakiUplinkRow("本社", "Q2XX-1111-1111", name, text, kind, "", "", "", status);
    }
}
