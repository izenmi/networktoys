using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

public class CiscoCsvConverterTests
{
    // ===== サンプル =====

    /// <summary>show interfaces status は桁揃えの表なので、ヘッダと行を同じ桁で組む。</summary>
    private static string StatusRow(string port, string name, string status, string vlan, string duplex, string speed, string type)
        => port.PadRight(10) + name.PadRight(19) + status.PadRight(13) + vlan.PadRight(11) + duplex.PadRight(8) + speed.PadRight(6) + type;

    private static string StatusSample(params string[] rows)
        => StatusRow("Port", "Name", "Status", "Vlan", "Duplex", "Speed", "Type") + "\n" + string.Join("\n", rows);

    private const string InventorySample = """
        NAME: "1", DESCR: "WS-C2960X-24TS-L"
        PID: WS-C2960X-24TS-L  , VID: V04  , SN: FOC1234X56Y

        NAME: "TenGigabitEthernet1/0/1", DESCR: "SFP-10GBase-SR, short reach"
        PID:                   , VID:      , SN:
        """;

    private const string VersionClassic = """
        Cisco IOS Software, C2960X Software (C2960X-UNIVERSALK9-M), Version 15.2(4)E10, RELEASE SOFTWARE (fc2)
        Technical Support: http://www.cisco.com/techsupport

        switch01 uptime is 2 years, 10 weeks, 4 days
        System image file is "flash:/c2960x-universalk9-mz.152-4.E10.bin"
        Last reload reason: power-on

        cisco WS-C2960X-24TS-L (APM86XXX) processor (revision B0) with 524288K bytes of memory.
        Processor board ID FOC9999X99Y

        Model Number                    : WS-C2960X-24TS-L
        System Serial Number            : FOC1234X56Y
        """;

    private const string VersionXe = """
        Cisco IOS XE Software, Version 17.03.05
        Cisco IOS Software [Amsterdam], Catalyst L3 Switch Software (CAT9K_IOSXE), Version 17.3.5, RELEASE SOFTWARE (fc1)

        router01 uptime is 5 days, 1 hour, 2 minutes
        """;

    private const string LoggingSample = """
        Syslog logging: enabled (0 messages dropped, 3 messages rate-limited)
            Console logging: level debugging, 123 messages logged
        Log Buffer (4096 bytes):
        *Mar  1 00:01:23.456: %LINK-3-UPDOWN: Interface GigabitEthernet0/1, changed state to down
        000123: Jun 10 12:34:56.789 JST: %SYS-5-CONFIG_I: Configured from console by admin on vty0
        %SYS-5-RELOAD: Reload requested by admin
        May  5 11:11:11: %LINEPROTO-5-UPDOWN: Line protocol on Interface Vlan10, changed state to up at 100% load
        """;

    private const string RouteSample = """
        Codes: L - local, C - connected, S - static, O - OSPF
        Gateway of last resort is 10.0.0.1 to network 0.0.0.0
        O        10.1.1.0/24 [110/2] via 10.0.0.2, GigabitEthernet0/1
        C        10.0.0.0/30 is directly connected, GigabitEthernet0/1
        """;

    // ===== Detect =====

    [Fact]
    public void Each_command_is_detected_by_its_own_header()
    {
        Assert.Equal(CiscoCommandKind.Inventory, CiscoCsvConverter.Detect(InventorySample));
        Assert.Equal(CiscoCommandKind.Version, CiscoCsvConverter.Detect(VersionClassic));
        Assert.Equal(CiscoCommandKind.Version, CiscoCsvConverter.Detect(VersionXe));
        Assert.Equal(CiscoCommandKind.InterfacesStatus, CiscoCsvConverter.Detect(StatusSample()));
        Assert.Equal(CiscoCommandKind.Logging, CiscoCsvConverter.Detect(LoggingSample));
        Assert.Equal(CiscoCommandKind.IpRoute, CiscoCsvConverter.Detect(RouteSample));

        Assert.Equal(CiscoCommandKind.InterfaceBrief, CiscoCsvConverter.Detect(
            "Interface              IP-Address      OK? Method Status                Protocol\n" +
            "GigabitEthernet0/1     10.0.0.2        YES NVRAM  up                    up"));

        Assert.Equal(CiscoCommandKind.CdpNeighbors, CiscoCsvConverter.Detect(
            "Capability Codes: R - Router, S - Switch\n" +
            "Device ID        Local Intrfce     Holdtme    Capability  Platform  Port ID"));

        Assert.Equal(CiscoCommandKind.MacTable, CiscoCsvConverter.Detect(
            "          Mac Address Table\n-------------------------------------------\n" +
            "Vlan    Mac Address       Type        Ports\n   1    0011.2233.4455    DYNAMIC     Gi0/1"));
    }

    [Fact]
    public void Plain_text_and_empty_input_stay_undetected()
    {
        Assert.Null(CiscoCsvConverter.Detect(null));
        Assert.Null(CiscoCsvConverter.Detect("  \n \n"));
        Assert.Null(CiscoCsvConverter.Detect("これはただのメモです。\n特別なヘッダはありません。"));
    }

    [Fact]
    public void Route_output_with_stray_log_lines_still_reads_as_routes()
    {
        string mixed = RouteSample + "\n*Mar  1 00:02:00: %OSPF-5-ADJCHG: Process 1, Nbr 10.0.0.2 on GigabitEthernet0/1 from LOADING to FULL";

        Assert.Equal(CiscoCommandKind.IpRoute, CiscoCsvConverter.Detect(mixed));
    }

    // ===== 既存パーサの写像 =====

    [Fact]
    public void Routes_become_rows_with_ad_and_metric_split()
    {
        CsvTable table = CiscoCsvConverter.Convert(CiscoCommandKind.IpRoute, RouteSample);

        Assert.Equal(new[] { "宛先", "プロトコル", "AD", "メトリック", "ネクストホップ", "インターフェース" }, table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("10.1.1.0/24", table.Rows[0][0]);
        Assert.Equal("110", table.Rows[0][2]);
        Assert.Equal("2", table.Rows[0][3]);
        Assert.Equal("10.0.0.2", table.Rows[0][4]);
    }

    // ===== show interfaces status =====

    [Fact]
    public void Interfaces_status_slices_by_header_columns()
    {
        string text = StatusSample(
            StatusRow("Gi1/0/1", "uplink to core", "connected", "trunk", "a-full", "a-1000", "10/100/1000BaseTX"),
            StatusRow("Gi1/0/2", "", "notconnect", "10", "auto", "auto", "10/100/1000BaseTX"),
            StatusRow("Gi1/0/3", "printer", "err-disabled", "20", "auto", "auto", "10/100/1000BaseTX"));

        CsvTable table = CiscoCsvConverter.Convert(CiscoCommandKind.InterfacesStatus, text);

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(new[] { "Gi1/0/1", "uplink to core", "connected", "trunk", "a-full", "a-1000", "10/100/1000BaseTX" }, table.Rows[0]);
        Assert.Equal("", table.Rows[1][1]);
        Assert.Equal("err-disabled", table.Rows[2][2]);
    }

    [Fact]
    public void Short_lines_and_rulers_do_not_break_the_status_table()
    {
        string text = StatusSample(
            new string('-', 60),
            "Gi1/0/4   mgmt");

        CsvTable table = CiscoCsvConverter.Convert(CiscoCommandKind.InterfacesStatus, text);

        CsvTable single = table;
        Assert.Single(single.Rows);
        Assert.Equal("Gi1/0/4", single.Rows[0][0]);
        Assert.Equal("mgmt", single.Rows[0][1]);
        Assert.Equal("", single.Rows[0][6]);   // 行が短ければ空欄
    }

    [Fact]
    public void Status_without_a_header_yields_no_rows()
    {
        CsvTable table = CiscoCsvConverter.Convert(CiscoCommandKind.InterfacesStatus, "ただのテキスト");

        Assert.Empty(table.Rows);
    }

    // ===== show inventory =====

    [Fact]
    public void Inventory_pairs_name_and_pid_lines()
    {
        CsvTable table = CiscoCsvConverter.Convert(CiscoCommandKind.Inventory, InventorySample);

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new[] { "1", "WS-C2960X-24TS-L", "WS-C2960X-24TS-L", "V04", "FOC1234X56Y" }, table.Rows[0]);
        Assert.Equal("SFP-10GBase-SR, short reach", table.Rows[1][1]);   // DESCR 内のカンマ
        Assert.Equal("", table.Rows[1][2]);   // PID 空
        Assert.Equal("", table.Rows[1][4]);   // SN 空
    }

    // ===== show version =====

    [Fact]
    public void Version_reads_the_classic_ios_fields()
    {
        CsvTable table = CiscoCsvConverter.Convert(CiscoCommandKind.Version, VersionClassic);
        var map = table.Rows.ToDictionary(r => r[0], r => r[1]);

        Assert.Equal("switch01", map["ホスト名"]);
        Assert.Equal("15.2(4)E10", map["IOS バージョン"]);
        Assert.Equal("WS-C2960X-24TS-L", map["モデル"]);
        Assert.Equal("FOC1234X56Y", map["シリアル"]);   // System Serial Number が Processor board ID より優先
        Assert.Equal("2 years, 10 weeks, 4 days", map["uptime"]);
        Assert.Equal("flash:/c2960x-universalk9-mz.152-4.E10.bin", map["System image"]);
        Assert.Equal("power-on", map["再起動理由"]);
    }

    [Fact]
    public void Version_omits_fields_that_are_not_present()
    {
        CsvTable table = CiscoCsvConverter.Convert(CiscoCommandKind.Version, VersionXe);
        var labels = table.Rows.Select(r => r[0]).ToList();

        Assert.Contains("ホスト名", labels);
        Assert.Contains("IOS バージョン", labels);
        Assert.DoesNotContain("モデル", labels);
        Assert.DoesNotContain("シリアル", labels);
        Assert.DoesNotContain("再起動理由", labels);
    }

    // ===== show logging =====

    [Fact]
    public void Log_lines_split_into_time_and_message_parts()
    {
        CsvTable table = CiscoCsvConverter.Convert(CiscoCommandKind.Logging, LoggingSample);

        Assert.Equal(4, table.Rows.Count);   // ヘッダ 3 行は読み飛ばす

        Assert.Equal("*Mar  1 00:01:23.456", table.Rows[0][0]);
        Assert.Equal("LINK", table.Rows[0][1]);
        Assert.Equal("3", table.Rows[0][2]);
        Assert.Equal("UPDOWN", table.Rows[0][3]);

        Assert.Equal("000123: Jun 10 12:34:56.789 JST", table.Rows[1][0]);   // seq 番号付き
        Assert.Equal("", table.Rows[2][0]);                                   // 時刻なし
        Assert.EndsWith("at 100% load", table.Rows[3][4]);                    // メッセージ内の % は素通し
    }

    // ===== ToCsv =====

    [Fact]
    public void Csv_escaping_covers_commas_quotes_newlines_and_formulas()
    {
        var table = new CsvTable(
            ["列A", "列B"],
            [
                ["a,b", "彼の\"名前\""],
                ["=SUM(A1)", "1行目\n2行目"],
            ]);

        string csv = table.ToCsv();
        string[] lines = csv.Split("\r\n");

        Assert.Equal("列A,列B", lines[0]);
        Assert.Equal("\"a,b\",\"彼の\"\"名前\"\"\"", lines[1]);
        Assert.StartsWith("'=SUM(A1)", lines[2]);        // 数式の無害化(既存 Quote の回帰)
        Assert.Contains("\"1行目\n2行目\"", csv);
        Assert.EndsWith("\r\n", csv);
    }
}
