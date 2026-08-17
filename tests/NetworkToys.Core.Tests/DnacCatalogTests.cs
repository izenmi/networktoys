using System.Text.Json;
using NetworkToys.Core.Assurance;
using NetworkToys.Core.Design;
using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// Catalyst Center の応答の読み取り。実機も CI も Catalyst Center を持たないので、
/// <b>ここがこのタブの正しさの拠り所</b>になる（ACI・WLC の検査と同じ位置づけ）。
/// 見本の JSON は実機が返す形をそのまま縮めたもの。
/// </summary>
public class DnacCatalogTests
{
    // ===== 見本 =====

    /// <summary>有線。健全度は <b>配列</b>で返る。ポートは userDetails 側にある。</summary>
    private const string WiredJson = """
        {"version":"1.0","response":[{
          "userDetails":{
            "id":"AA:BB:CC:DD:EE:01","hostType":"WIRED","hostName":"pc-1234",
            "hostMac":"AA:BB:CC:DD:EE:01","hostIpV4":"10.10.22.98",
            "lastUpdated":1723852800000,"vlanId":"550","port":"GigabitEthernet1/0/13",
            "location":"Global/Tokyo/3F","clientConnection":"sw-3f-01",
            "healthScore":[{"healthType":"OVERALL","reason":"","score":10}]},
          "connectedDevice":[{"deviceDetails":{
            "family":"Switches","type":"Cisco Catalyst 9300","hostname":"sw-3f-01",
            "managementIpAddress":"10.10.22.66","serialNumber":"FCW1234A0AB",
            "collectionStatus":"Managed","role":"ACCESS"}}]}]}
        """;

    /// <summary>無線。SSID と周波数は userDetails、AP 名は connectedDevice。</summary>
    private const string WirelessJson = """
        {"version":"1.0","response":[{
          "userDetails":{
            "id":"AA:BB:CC:DD:EE:02","hostType":"WIRELESS","hostName":"note-77",
            "hostMac":"AA:BB:CC:DD:EE:02","hostIpV4":"10.20.30.44",
            "lastUpdated":1723852800000,"ssid":"CorpSSID","frequency":"5.0",
            "location":"Global/Tokyo/5F","clientConnection":"ap-5f-03","vlanId":"600",
            "healthScore":[{"healthType":"OVERALL","reason":"","score":6}]},
          "connectedDevice":[{"deviceDetails":{
            "family":"Unified AP","type":"Cisco Catalyst 9130AXI","hostname":"ap-5f-03",
            "managementIpAddress":"10.20.0.13"}}]}]}
        """;

    /// <summary>版が古いときの <c>client-detail</c>。包みが無く、節が 2 つに割れている。</summary>
    private const string ClientDetailJson = """
        {"detail":{
           "id":"AA:BB:CC:DD:EE:02","hostType":"WIRELESS","hostName":"note-77",
           "hostMac":"AA:BB:CC:DD:EE:02","hostIpV4":"10.20.30.44",
           "lastUpdated":1723852800000,"ssid":"CorpSSID","clientConnection":"ap-5f-03",
           "location":"Global/Tokyo/5F","vlanId":"600",
           "healthScore":[{"healthType":"OVERALL","score":6}]},
         "connectionInfo":{
           "hostType":"WIRELESS","nwDeviceName":"wlc-01","band":"5.0","channel":"36"},
         "topology":{"nodes":[],"links":[]}}
        """;

    private const string EventsJson = """
        {"version":"1.0","response":[
          {"timestamp":1723852800000,"name":"Onboarding","eventStatus":"SUCCESS",
           "networkDeviceName":"ap-5f-03","ssid":"CorpSSID","reasonType":""},
          {"timestamp":1723852860000,"name":"AAA Authentication","eventStatus":"FAIL",
           "networkDeviceName":"ap-5f-03","failureCategory":"AAA サーバ無応答"},
          {"timestamp":1723852900000,"name":"Roaming","eventStatus":"SOMETHING_NEW",
           "ssid":"CorpSSID"}]}
        """;

    private const string InventoryJson = """
        {"version":"1.0","response":[
          {"id":"uuid-1","hostname":"sw-3f-01","platformId":"C9300-48P","serialNumber":"FCW1234A0AB",
           "softwareVersion":"17.9.4","managementIpAddress":"10.10.22.66",
           "siteHierarchy":"Global/Tokyo/3F","role":"ACCESS","reachabilityStatus":"Reachable"},
          {"id":"uuid-2","hostname":"sw-4f-01","platformId":"C9300-24P","serialNumber":"FCW1234A0CD",
           "softwareVersion":"17.9.4","managementIpAddress":"10.10.22.67",
           "siteHierarchy":"Global/Tokyo/4F","role":"ACCESS","reachabilityStatus":"Unreachable"},
          {"id":"uuid-3","hostname":"sw-5f-01","platformId":"C9300-24P","serialNumber":"FCW1234A0EF",
           "softwareVersion":"17.9.4","managementIpAddress":"10.10.22.68",
           "siteHierarchy":"Global/Tokyo/5F","role":"ACCESS","reachabilityStatus":"Reachable"}]}
        """;

    /// <summary>1 台目は id で、2 台目は id を持たず管理 IP でしか結べない。3 台目は健全度が無い。</summary>
    private const string DeviceHealthJson = """
        {"version":"1.0","response":[
          {"id":"uuid-1","name":"sw-3f-01","overallHealth":9},
          {"name":"sw-4f-01","managementIpAddress":"10.10.22.67","overallHealth":3}]}
        """;

    private const string EoxJson = """
        {"version":"1.0","response":[
          {"deviceId":"uuid-1","scanStatus":"SUCCESS","alertCount":2,
           "eoxDetails":[{"eoxPhysicalType":"HARDWARE","lastDateOfSupport":"2030-01-31",
                          "bulletinName":"EOL12345"}]},
          {"deviceId":"uuid-2","scanStatus":"NOT_SCANNED"}]}
        """;

    private const string ComplianceJson = """
        {"version":"1.0","response":[
          {"deviceUuid":"uuid-1","complianceType":"RUNNING_CONFIG","status":"NON_COMPLIANT",
           "lastSyncTime":1723852800000,"message":"起動時の設定と差があります"}]}
        """;

    private const string LicenseJson = """
        {"version":"1.0","response":[
          {"device_uuid":"uuid-1","device_name":"sw-3f-01","dna_level":"ADVANTAGE",
           "registration_status":"REGISTERED","license_expiry_date":"2027-03-31",
           "virtual_account_name":"DEFAULT"}]}
        """;

    // ===== 入力の見分け =====

    [Theory]
    [InlineData("10.10.22.98", DnacEntityKind.Ip)]
    [InlineData("aabb.ccdd.eeff", DnacEntityKind.Mac)]
    [InlineData("AA-BB-CC-DD-EE-FF", DnacEntityKind.Mac)]
    [InlineData("aa:bb:cc:dd:ee:ff", DnacEntityKind.Mac)]
    [InlineData("2001:db8::1", DnacEntityKind.Ip)]
    [InlineData("pc-1234", DnacEntityKind.Unknown)]
    [InlineData("", DnacEntityKind.Unknown)]
    [InlineData("aabb.ccdd.eeg", DnacEntityKind.Unknown)]
    public void 入力が_IP_か_MAC_かを見分ける(string input, DnacEntityKind expected)
        => Assert.Equal(expected, DnacCatalog.EntityKindOf(input));

    [Fact]
    public void 見分けた種別を_Catalyst_Center_の言葉に直す()
    {
        Assert.Equal("ip_address", DnacCatalog.EntityTypeOf(DnacEntityKind.Ip));
        Assert.Equal("mac_address", DnacCatalog.EntityTypeOf(DnacEntityKind.Mac));
        Assert.Equal("", DnacCatalog.EntityTypeOf(DnacEntityKind.Unknown));
    }

    [Fact]
    public void 比べるための_MAC_は区切りを落として小文字にする()
    {
        Assert.Equal("aabbccddeeff", DnacCatalog.NormalizeMac("AA:BB:CC:DD:EE:FF"));
        Assert.Equal("aabbccddeeff", DnacCatalog.NormalizeMac("aabb.ccdd.eeff"));
        Assert.Equal("", DnacCatalog.NormalizeMac(null));
    }

    // ===== 端末の接続先 =====

    [Fact]
    public void 有線の端末から機器とポートと_VLAN_を出す()
    {
        DnacConnectionRow row = Assert.Single(DnacCatalog.ParseConnections(DnacJson.Rows(WiredJson)));

        Assert.Equal("AA:BB:CC:DD:EE:01", row.Mac);
        Assert.Equal("10.10.22.98", row.Ip);
        Assert.Equal("有線", row.Kind);
        Assert.Equal("sw-3f-01", row.Device);
        Assert.Equal("GigabitEthernet1/0/13", row.Port);
        Assert.Equal("550", row.Vlan);
        Assert.Equal("", row.Ssid);
        Assert.Equal(SeverityKind.Ok, row.HealthKind);
        Assert.Contains("10", row.Health, StringComparison.Ordinal);
        Assert.Equal("Global/Tokyo/3F", row.Site);
        Assert.NotEqual("", row.Updated);
    }

    [Fact]
    public void 無線の端末から_AP_と_SSID_と帯域を出す()
    {
        DnacConnectionRow row = Assert.Single(DnacCatalog.ParseConnections(DnacJson.Rows(WirelessJson)));

        Assert.Equal("無線", row.Kind);
        Assert.Equal("ap-5f-03", row.Device);
        Assert.Equal("CorpSSID", row.Ssid);
        Assert.Equal("5GHz", row.Band);
        Assert.Equal(SeverityKind.Notice, row.HealthKind);
    }

    /// <summary>
    /// 版が違って応答の形が変わっても、<b>候補パスが両方を覆っているので同じ行が出る</b>。
    /// ここが崩れると「実機だけ表が空」になる。
    /// </summary>
    [Fact]
    public void client_detail_の形でも同じ行になる()
    {
        DnacConnectionRow enrichment =
            Assert.Single(DnacCatalog.ParseConnections(DnacJson.Rows(WirelessJson)));
        DnacConnectionRow detail =
            Assert.Single(DnacCatalog.ParseClientDetail(DnacJson.One(ClientDetailJson)));

        Assert.Equal(enrichment.Mac, detail.Mac);
        Assert.Equal(enrichment.Ip, detail.Ip);
        Assert.Equal(enrichment.Kind, detail.Kind);
        Assert.Equal(enrichment.Device, detail.Device);
        Assert.Equal(enrichment.Ssid, detail.Ssid);
        Assert.Equal(enrichment.Band, detail.Band);
        Assert.Equal(enrichment.Vlan, detail.Vlan);
        Assert.Equal(enrichment.Health, detail.Health);
        Assert.Equal(enrichment.Site, detail.Site);
    }

    [Fact]
    public void 項目が欠けていても例外にならず空文字になる()
    {
        DnacConnectionRow row = Assert.Single(
            DnacCatalog.ParseConnections(DnacJson.Rows("""{"response":[{"userDetails":{}}]}""")));

        Assert.Equal("", row.Mac);
        Assert.Equal("", row.Device);
        Assert.Equal("", row.Updated);
        Assert.Equal("—", row.Health);
        Assert.Equal(SeverityKind.Muted, row.HealthKind);
    }

    [Fact]
    public void 読めない応答は_0_件にする()
    {
        Assert.Empty(DnacJson.Rows("これは JSON ではない"));
        Assert.Empty(DnacCatalog.ParseConnections(DnacJson.Rows(null)));
        Assert.Empty(DnacCatalog.ParseClientDetail(null));
    }

    [Theory]
    [InlineData(10, SeverityKind.Ok)]
    [InlineData(8, SeverityKind.Ok)]
    [InlineData(7, SeverityKind.Notice)]
    [InlineData(4, SeverityKind.Notice)]
    [InlineData(3, SeverityKind.Alert)]
    [InlineData(1, SeverityKind.Alert)]
    [InlineData(null, SeverityKind.Muted)]
    public void 健全度は_1_から_10_で色分けする(int? score, SeverityKind expected)
        => Assert.Equal(expected, DnacCatalog.DescribeHealth(score).Kind);

    [Fact]
    public void 健全度が取れないときは_0_点と区別する()
        => Assert.Equal("—", DnacCatalog.DescribeHealth(null).Text);

    // ===== イベント =====

    [Fact]
    public void イベントの成否と時刻を読む()
    {
        IReadOnlyList<DnacEventRow> rows = DnacCatalog.ParseEvents(DnacJson.Rows(EventsJson));

        Assert.Equal(3, rows.Count);
        Assert.Equal(SeverityKind.Ok, rows[0].StatusKind);
        Assert.Equal("Onboarding", rows[0].Name);
        Assert.NotEqual("", rows[0].Time);

        Assert.Equal(SeverityKind.Alert, rows[1].StatusKind);
        Assert.Equal("AAA サーバ無応答", rows[1].Detail);

        // 知らない値はそのまま出す（勝手に成功や失敗へ寄せない）
        Assert.Equal("SOMETHING_NEW", rows[2].Status);
        Assert.Equal(SeverityKind.Muted, rows[2].StatusKind);
    }

    [Fact]
    public void イベントの_API_が無い版では問題を代わりに並べる()
    {
        const string json = """
            {"response":[{"userDetails":{"id":"x"},"issueDetails":{"issue":[
              {"issueId":"i1","issueSummary":"AP との接続が不安定です",
               "issueTimestamp":1723852800000,"issueCategory":"Onboarding",
               "issueDescription":"再認証が繰り返されています"}]}}]}
            """;

        DnacEventRow row = Assert.Single(DnacCatalog.ParseIssuesAsEvents(DnacJson.Rows(json)));

        Assert.Equal("AP との接続が不安定です", row.Name);
        Assert.Equal(SeverityKind.Notice, row.StatusKind);
        Assert.NotEqual("", row.Time);
    }

    // ===== 機器 =====

    [Fact]
    public void 在庫と健全度を_id_と管理_IP_の両方で突き合わせる()
    {
        IReadOnlyList<DnacDeviceRow> rows = DnacCatalog.ParseDevices(
            DnacJson.Rows(InventoryJson), DnacJson.Rows(DeviceHealthJson));

        // 健全度を持たない機器も落とさない
        Assert.Equal(3, rows.Count);

        Assert.Equal("sw-3f-01", rows[0].Name);
        Assert.Equal(SeverityKind.Ok, rows[0].HealthKind);       // id で結んだ
        Assert.Equal(SeverityKind.Ok, rows[0].ReachabilityKind);

        Assert.Equal(SeverityKind.Alert, rows[1].HealthKind);    // 管理 IP で結んだ
        Assert.Equal(SeverityKind.Alert, rows[1].ReachabilityKind);

        Assert.Equal("—", rows[2].Health);                       // 健全度が無い
        Assert.Equal("C9300-24P", rows[2].Model);
        Assert.Equal("FCW1234A0EF", rows[2].Serial);
    }

    [Fact]
    public void 健全度が取れなくても在庫だけは必ず出す()
    {
        IReadOnlyList<DnacDeviceRow> rows = DnacCatalog.ParseDevices(DnacJson.Rows(InventoryJson));

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("—", r.Health));
    }

    [Fact]
    public void 知らない到達性はそのまま出す()
    {
        Assert.Equal("Something New", DnacCatalog.DescribeReachability("Something New").Text);
        Assert.Equal(SeverityKind.Muted, DnacCatalog.DescribeReachability("Something New").Kind);
        Assert.Equal(SeverityKind.Notice, DnacCatalog.DescribeReachability("Ping Reachable").Kind);
    }

    // ===== 保守と適合 =====

    [Fact]
    public void EoX_は未スキャンと対象なしを混ぜない()
    {
        IReadOnlyList<DnacLifecycleRow> rows = DnacCatalog.ParseEox(DnacJson.Rows(EoxJson));

        Assert.Equal(2, rows.Count);
        Assert.Equal("uuid-1", rows[0].Device);
        Assert.Equal("HARDWARE", rows[0].Kind);
        Assert.Equal("2030-01-31", rows[0].Date);
        Assert.Equal("EOL12345", rows[0].Note);
        Assert.Equal(SeverityKind.Notice, rows[0].StateKind);

        Assert.Contains("未スキャン", rows[1].State, StringComparison.Ordinal);
    }

    [Fact]
    public void 適合性とライセンスを同じ列に寄せる()
    {
        DnacLifecycleRow compliance = Assert.Single(DnacCatalog.ParseCompliance(DnacJson.Rows(ComplianceJson)));
        DnacLifecycleRow license = Assert.Single(DnacCatalog.ParseLicenses(DnacJson.Rows(LicenseJson)));

        Assert.Equal("uuid-1", compliance.Device);
        Assert.Equal("RUNNING_CONFIG", compliance.Kind);
        Assert.Equal(SeverityKind.Alert, compliance.StateKind);
        Assert.NotEqual("", compliance.Date);

        Assert.Equal("sw-3f-01", license.Device);
        Assert.Equal("ADVANTAGE", license.Kind);
        Assert.Equal(SeverityKind.Ok, license.StateKind);
        Assert.Equal("2027-03-31", license.Date);

        // 3 種を 1 枚の表に混ぜるので、列数が揃っていることが前提になる
        Assert.Equal(
            DnacCatalog.ToCsv(DnacCatalog.ParseEox(DnacJson.Rows(EoxJson))).Headers.Count,
            DnacCatalog.ToCsv(new[] { compliance, license }).Headers.Count);
    }

    // ===== 問い合わせ先 =====

    [Fact]
    public void 機器一覧の_offset_は_1_始まり()
    {
        Assert.Equal("/dna/intent/api/v1/network-device?offset=1&limit=500", DnacCatalog.DevicePath(0, 500));
        Assert.Equal("/dna/intent/api/v1/network-device?offset=501&limit=500", DnacCatalog.DevicePath(1, 500));
    }

    [Fact]
    public void 候補は前のものから順に並べる()
    {
        Assert.StartsWith("/dna/data/api/v1/networkDevices", DnacCatalog.DeviceHealthPaths[0], StringComparison.Ordinal);
        Assert.All(DnacCatalog.EoxPaths, p => Assert.StartsWith("/dna/", p, StringComparison.Ordinal));
        Assert.All(DnacCatalog.CompliancePaths, p => Assert.StartsWith("/dna/", p, StringComparison.Ordinal));
        Assert.All(DnacCatalog.LicensePaths, p => Assert.StartsWith("/dna/", p, StringComparison.Ordinal));
        Assert.All(DnacCatalog.ClientEnrichmentPaths, p => Assert.StartsWith("/dna/", p, StringComparison.Ordinal));
    }

    [Fact]
    public void 入力した値は問い合わせ先に埋め込む前に逃がす()
        => Assert.Contains("aabb.ccdd.eeff", DnacCatalog.ClientDetailPath("aabb.ccdd.eeff", 1), StringComparison.Ordinal);

    // ===== CLI（参照のみ） =====

    [Fact]
    public void 許可コマンドの一覧は重複を落として並べ替える()
    {
        const string json = """
            {"response":["show","show","dir",{"command":"ping"}]}
            """;

        Assert.Equal(new[] { "dir", "ping", "show" }, DnacCatalog.ParseLegitReads(DnacJson.Rows(json)));
    }

    [Fact]
    public void 読み取り要求には機器とコマンドだけを載せる()
    {
        string body = DnacCatalog.ReadRequestBody(["uuid-1"], ["show version"]);

        using JsonDocument document = JsonDocument.Parse(body);

        Assert.Equal("uuid-1", document.RootElement.GetProperty("deviceUuids")[0].GetString());
        Assert.Equal("show version", document.RootElement.GetProperty("commands")[0].GetString());
    }

    [Fact]
    public void 追跡の結果から出力のファイルを取り出す()
    {
        Assert.Equal("task-9", DnacCatalog.ParseTaskId("""{"response":{"taskId":"task-9"}}"""));

        // fileId は progress の中に「文字列の JSON」として入っている
        (bool done, string fileId, string? error) = DnacCatalog.ParseTask(
            """{"response":{"id":"task-9","progress":"{\"fileId\":\"file-3\"}"}}""");

        Assert.True(done);
        Assert.Equal("file-3", fileId);
        Assert.Null(error);

        (bool failedDone, _, string? failure) = DnacCatalog.ParseTask(
            """{"response":{"id":"task-9","isError":true,"failureReason":"権限がありません"}}""");

        Assert.True(failedDone);
        Assert.Equal("権限がありません", failure);

        // まだ終わっていない
        Assert.False(DnacCatalog.ParseTask("""{"response":{"id":"task-9","progress":"実行中"}}""").Done);
    }

    [Fact]
    public void CLI_の出力は解釈せずそのまま見せる()
    {
        const string json = """
            {"response":[{"deviceUuid":"uuid-1",
              "commandResponses":{"SUCCESS":"Cisco IOS XE Software, Version 17.9.4","FAILURE":""}}]}
            """;

        string text = DnacCatalog.RenderCliOutput(json);

        Assert.Contains("uuid-1", text, StringComparison.Ordinal);
        Assert.Contains("Version 17.9.4", text, StringComparison.Ordinal);
    }

    // ===== 応答コード =====

    [Fact]
    public void 失敗はコードから文言を組み立てる()
    {
        Assert.Contains("401", DnacCatalog.DescribeFailure(401), StringComparison.Ordinal);
        Assert.Contains("404", DnacCatalog.DescribeFailure(404), StringComparison.Ordinal);
        Assert.Contains("503", DnacCatalog.DescribeFailure(503), StringComparison.Ordinal);
    }

    // ===== CSV =====

    [Fact]
    public void CSV_の列数が見出しと揃っている()
    {
        CsvTable[] tables =
        [
            DnacCatalog.ToCsv(DnacCatalog.ParseConnections(DnacJson.Rows(WiredJson))),
            DnacCatalog.ToCsv(DnacCatalog.ParseEvents(DnacJson.Rows(EventsJson))),
            DnacCatalog.ToCsv(DnacCatalog.ParseDevices(DnacJson.Rows(InventoryJson), DnacJson.Rows(DeviceHealthJson))),
            DnacCatalog.ToCsv(DnacCatalog.ParseEox(DnacJson.Rows(EoxJson))),
        ];

        Assert.All(tables, t =>
        {
            Assert.NotEmpty(t.Rows);
            Assert.All(t.Rows, r => Assert.Equal(t.Headers.Count, r.Length));
        });
    }
}
