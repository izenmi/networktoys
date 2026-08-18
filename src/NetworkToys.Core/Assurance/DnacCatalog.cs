using System.Globalization;
using System.Net;
using System.Text.Json;
using NetworkToys.Core.Design;
using NetworkToys.Core.Terminal;
using NetworkToys.Core.Work;

namespace NetworkToys.Core.Assurance;

/// <summary>探すものが IP か MAC か。どちらでもなければ <see cref="Unknown"/>。</summary>
public enum DnacEntityKind
{
    Unknown,
    Ip,
    Mac,
}

/// <summary>端末の接続先の 1 行。有線と無線を同じ形で並べる。</summary>
public sealed record DnacConnectionRow(
    string Mac,
    string Ip,
    string HostName,
    string Kind,
    string Device,
    string Port,
    string Vlan,
    string Ssid,
    string Band,
    string Health,
    SeverityKind HealthKind,
    string Site,
    string Updated);

/// <summary>端末に起きたことの 1 行（Assurance の 360 で見える種類のもの）。</summary>
public sealed record DnacEventRow(
    string Time,
    string Name,
    string Status,
    SeverityKind StatusKind,
    string Source,
    string Detail);

/// <summary>機器の 1 行。在庫と健全度を 1 行に畳む。</summary>
public sealed record DnacDeviceRow(
    string Id,
    string Name,
    string Model,
    string Serial,
    string Version,
    string Ip,
    string Site,
    string Role,
    string Reachability,
    SeverityKind ReachabilityKind,
    string Health,
    SeverityKind HealthKind);

/// <summary>保守と適合の 1 行。EoX・適合性・ライセンスを同じ 5 列に寄せる。</summary>
public sealed record DnacLifecycleRow(
    string Device,
    string Kind,
    string State,
    SeverityKind StateKind,
    string Date,
    string Note);

/// <summary>
/// Catalyst Center の応答を画面と CSV の行にする。
///
/// ここは HTTP に触らない（取ってくるのは App 側の <c>Services/DnacClient</c>）。
/// <b>リーフ名とパスの候補はすべてこのファイルの上の方に並べてある。</b>
/// 実機で外れたら、そこへ 1 行足すだけで直るようにしてある。
/// </summary>
public static class DnacCatalog
{
    // ===== 問い合わせ先（版で割れるものは候補を並べる。順に試す） =====

    private const string Intent = "/dna/intent/api/v1";
    private const string Data = "/dna/data/api/v1";

    public const string TokenPath = "/dna/system/api/v1/auth/token";

    /// <summary>端末の接続先。<b>entity_type / entity_value は「ヘッダ」で渡す</b>。</summary>
    public static string[] ClientEnrichmentPaths => [$"{Intent}/client-enrichment-details"];

    /// <summary>版が古いときの逃げ道（MAC のときだけ引ける）。</summary>
    public static string ClientDetailPath(string mac, long timestampMs)
        => $"{Intent}/client-detail?macAddress={Uri.EscapeDataString(mac)}&timestamp={timestampMs}";

    /// <summary>
    /// 端末のイベント。<b><c>/dna/data/api/v1</c> は 2.3.7.6 以降</b>にしか無い。
    /// </summary>
    public static string EventsPath(string mac, long startMs, long endMs, int limit = 100)
        => $"{Data}/assuranceEvents?clientMac={Uri.EscapeDataString(mac)}"
           + $"&startTime={startMs}&endTime={endMs}&limit={limit}";

    /// <summary>
    /// 古い版（2.3.5 など）でイベントの代わりに使う「問題」。
    /// <b>MAC で絞れる</b>ので、端末 1 台ぶんだけが返る。
    /// </summary>
    public static string IssuesPath(string mac, long startMs, long endMs)
        => $"{Intent}/issues?macAddress={Uri.EscapeDataString(mac)}"
           + $"&startTime={startMs}&endTime={endMs}";

    /// <summary>
    /// 機器の在庫。<b><c>offset</c> は 1 始まり</b>（0 を渡すと版によっては何も返らない）。
    /// 引数は 0 始まりのページ番号にしてある — 呼ぶ側で 1 を足し忘れないため。
    /// </summary>
    public static string DevicePath(int page, int limit)
        => $"{Intent}/network-device?offset={(page * limit) + 1}&limit={limit}";

    /// <summary>
    /// 機器の健全度。<b>1 本目は 2.3.7.6 以降にしか無い</b>ので、
    /// 2.3.5 系では <c>device-health</c>（機器ごとの一覧）へ落ちる。
    /// <c>network-health</c> は拠点ごとの集計で機器の行を持たないため、最後の保険。
    /// </summary>
    public static string[] DeviceHealthPaths =>
    [
        $"{Data}/networkDevices",
        $"{Intent}/device-health?limit=1000&offset=1",
        $"{Intent}/network-health",
    ];

    /// <summary>
    /// 端末の一覧。<b>期間で絞る</b>（いつ見えた端末か）。offset は 1 始まり。
    ///
    /// <b><c>/dna/data/api/v1</c> の一群は 2.3.7.6 以降にしか無い。</b>
    /// それより古い版（2.3.5 など）では 404 になるので、Endpoint Analytics の
    /// 端末一覧へ落とす（そちらは接続先や品質を持たないぶん、出る項目が少ない）。
    /// </summary>
    public static string[] ClientsPaths(long startMs, long endMs, int page, int limit) =>
    [
        $"{Data}/clients?startTime={startMs}&endTime={endMs}"
            + $"&offset={(page * limit) + 1}&limit={limit}",
        $"{Intent}/endpoint-analytics/endpoints?limit={limit}&offset={(page * limit) + 1}",
    ];

    /// <summary>その一覧が古い版向けの逃げ道かどうか（画面に断りを出すため）。</summary>
    public static bool IsFallbackClientPath(string? path)
        => path is not null && path.Contains("endpoint-analytics", StringComparison.Ordinal);

    /// <summary>脆弱性（PSIRT）。手持ちの機器が対象になっている勧告が返る。</summary>
    public static string[] AdvisoryPaths =>
    [
        $"{Intent}/security-advisory/advisory",
        $"{Intent}/security-advisories/results/advisories",
    ];

    public static string[] EoxPaths => [$"{Intent}/eox-status/device"];
    public static string[] CompliancePaths => [$"{Intent}/compliance"];

    /// <summary>
    /// ライセンス。<b>page_number / limit / order は必須</b>で、付けないと 400 が返る
    /// （2026-08-17 に実機で確認）。付けない形は、版が違うときのために後ろへ残してある。
    /// </summary>
    public static string[] LicensePaths =>
    [
        $"{Intent}/licenses/device/summary?page_number=1&limit=500&order=asc",
        $"{Intent}/licenses/device/summary",
    ];

    // ===== リーフ名の候補（版で動く。ここに 1 行足せば直る） =====

    private static readonly string[] DeviceNameLeaf =
        ["name", "deviceDetails/name", "hostname", "nwDeviceName", "networkDeviceName"];

    private static readonly string[] PortLeaf =
        ["port", "interfaceName", "portName", "clientConnection/interfaceName"];

    private static readonly string[] ApLeaf = ["apName", "clientConnection", "apGroup"];

    // healthScore は <b>配列</b>で返る（[{"healthType":"OVERALL","score":10}]）。
    // DnacJson.First は途中の配列の先頭を見るので "healthScore/score" で拾える。
    private static readonly string[] HealthLeaf =
        ["healthScore/score", "healthScore", "overallHealth", "health", "healthScore/overallScore"];

    // ===== 探すものの見分け =====

    /// <summary>
    /// 入れられた文字が IP か MAC か。<b>どちらでもなければ Unknown</b>（勝手にどちらかへ倒さない）。
    /// MAC は <c>aabb.ccdd.eeff</c> / <c>AA-BB-…</c> / <c>aa:bb:…</c> のどれでも受ける。
    /// </summary>
    public static DnacEntityKind EntityKindOf(string? input)
    {
        string text = (input ?? "").Trim();

        if (text.Length == 0) return DnacEntityKind.Unknown;

        string mac = NormalizeMac(text);

        if (mac.Length == 12 && mac.All(Uri.IsHexDigit)) return DnacEntityKind.Mac;

        return IPAddress.TryParse(text, out _) ? DnacEntityKind.Ip : DnacEntityKind.Unknown;
    }

    /// <summary>Catalyst Center へ渡すときの <c>entity_type</c>。</summary>
    public static string EntityTypeOf(DnacEntityKind kind) => kind switch
    {
        DnacEntityKind.Ip => "ip_address",
        DnacEntityKind.Mac => "mac_address",
        _ => "",
    };

    /// <summary>比べるための MAC。<b>表示は機器が返した形のまま</b>にする。</summary>
    public static string NormalizeMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return "";

        var text = new System.Text.StringBuilder(mac.Length);

        foreach (char c in mac)
        {
            if (c is ':' or '-' or '.' or ' ') continue;

            text.Append(char.ToLowerInvariant(c));
        }

        return text.ToString();
    }

    // ===== 端末の接続先 =====

    /// <summary>
    /// <c>client-enrichment-details</c> の応答をほどく。
    ///
    /// 1 件の端末につき <c>connectedDevice</c> が複数返ることがある（無線なら AP、
    /// その先のスイッチ、というように）。<b>1 つずつ行にする</b> — まとめると
    /// 「どこに刺さっているか」が消える。
    /// </summary>
    public static IReadOnlyList<DnacConnectionRow> ParseConnections(IEnumerable<JsonElement> rows)
    {
        var list = new List<DnacConnectionRow>();

        foreach (JsonElement row in rows)
        {
            JsonElement user = Section(row, "userDetails") ?? row;

            string mac = DnacJson.First(user, "hostMac", "macAddress", "mac", "id");
            string ip = DnacJson.First(user, "hostIpV4", "hostIp", "ipAddress", "hostIpV6");
            string host = DnacJson.First(user, "hostName", "userId", "id");
            string ssid = DnacJson.First(user, "ssid", "connectedDevice/ssid");
            string band = DnacJson.First(user, "frequency", "band", "dataRate");
            string vlan = DnacJson.First(user, "vlanId", "vnid", "clientConnection/vlanId");
            string type = DnacJson.First(user, "hostType", "connectionType", "clientType");

            int? score = DnacJson.Int(user, HealthLeaf);
            (string health, SeverityKind healthKind) = DescribeHealth(score);

            IReadOnlyList<JsonElement> devices = DnacJson.Children(row, "connectedDevice");

            if (devices.Count == 0)
            {
                list.Add(new DnacConnectionRow(
                    Mac: mac, Ip: ip, HostName: host,
                    Kind: DescribeConnection(type),
                    Device: DnacJson.First(user, "connectedDevice/name", "clientConnection"),
                    Port: DnacJson.First(user, PortLeaf),
                    Vlan: vlan, Ssid: ssid, Band: DescribeBand(band),
                    Health: health, HealthKind: healthKind,
                    Site: DnacJson.First(user, "location", "siteHierarchy", "site"),
                    Updated: DescribeTime(DnacJson.Long(user, "lastUpdated", "timestamp"))));

                continue;
            }

            foreach (JsonElement device in devices)
            {
                JsonElement detail = Section(device, "deviceDetails") ?? device;

                // 項目の在り処は版で user 側だったり device 側だったりする。両方見る
                list.Add(new DnacConnectionRow(
                    Mac: mac,
                    Ip: ip,
                    HostName: host,
                    Kind: DescribeConnection(type),
                    Device: Or(DnacJson.First(detail, DeviceNameLeaf), DnacJson.First(user, ApLeaf)),
                    Port: Or(Pick(user, detail, PortLeaf), Pick(user, detail, ApLeaf)),
                    Vlan: Or(vlan, DnacJson.First(detail, "vlanId")),
                    Ssid: Or(ssid, DnacJson.First(detail, "ssid")),
                    Band: DescribeBand(Or(band, DnacJson.First(detail, "frequency", "band"))),
                    Health: health,
                    HealthKind: healthKind,
                    Site: Or(DnacJson.First(detail, "location", "siteHierarchy"),
                             DnacJson.First(user, "location", "siteHierarchy")),
                    Updated: DescribeTime(DnacJson.Long(user, "lastUpdated", "timestamp")
                                          ?? DnacJson.Long(detail, "lastUpdated", "timestamp"))));
            }
        }

        return list;
    }

    /// <summary>版が古くて <c>client-detail</c> しか無いときの読み取り。</summary>
    public static IReadOnlyList<DnacConnectionRow> ParseClientDetail(JsonElement? response)
    {
        if (response is not { } root) return [];

        JsonElement detail = Section(root, "detail") ?? root;
        JsonElement connection = Section(root, "connectionInfo") ?? root;

        int? score = DnacJson.Int(detail, HealthLeaf);
        (string health, SeverityKind healthKind) = DescribeHealth(score);

        return
        [
            new DnacConnectionRow(
                Mac: DnacJson.First(detail, "hostMac", "macAddress", "id"),
                Ip: DnacJson.First(detail, "hostIpV4", "hostIpV6", "ipAddress"),
                HostName: DnacJson.First(detail, "hostName", "userId"),
                Kind: DescribeConnection(DnacJson.First(detail, "hostType", "connectionType")),
                Device: Or(DnacJson.First(detail, ApLeaf), DnacJson.First(connection, "nwDeviceName")),
                Port: Pick(detail, connection, PortLeaf),
                Vlan: DnacJson.First(detail, "vlanId", "vnid"),
                Ssid: DnacJson.First(detail, "ssid"),
                Band: DescribeBand(Pick(connection, detail, "band", "frequency")),
                Health: health,
                HealthKind: healthKind,
                Site: DnacJson.First(detail, "location", "siteHierarchy"),
                Updated: DescribeTime(DnacJson.Long(detail, "lastUpdated", "timestamp"))),
        ];
    }

    /// <summary>
    /// 端末の一覧。<c>client-enrichment-details</c> と違って<b>1 件が平らな形</b>で返るので、
    /// 同じ行の型に寄せて読む（画面の表を 2 つ作らずに済む）。
    /// </summary>
    public static IReadOnlyList<DnacConnectionRow> ParseClients(IEnumerable<JsonElement> rows)
    {
        var list = new List<DnacConnectionRow>();

        foreach (JsonElement row in rows)
        {
            int? score = DnacJson.Int(row, "health/overallScore", "healthScore/score", "overallHealth", "health");
            (string health, SeverityKind healthKind) = DescribeHealth(score);

            list.Add(new DnacConnectionRow(
                Mac: DnacJson.First(row, "macAddress", "mac", "id"),
                Ip: DnacJson.First(row, "ipv4Address", "hostIpV4", "ipAddress", "ip"),
                HostName: DnacJson.First(row, "name", "hostName", "userId", "deviceType"),
                Kind: DescribeConnection(DnacJson.First(row, "type", "hostType", "connectionType")),
                Device: DnacJson.First(row, "connectedNetworkDevice/connectedNetworkDeviceName",
                                       "connectedNetworkDeviceName", "clientConnection", "apName"),
                Port: DnacJson.First(row, "connectedNetworkDevice/connectedInterfaceName",
                                     "connectedInterfaceName", "port"),
                Vlan: DnacJson.First(row, "vlanId", "vnid"),
                Ssid: DnacJson.First(row, "ssid", "connection/ssid"),
                Band: DescribeBand(DnacJson.First(row, "band", "frequency", "connection/band")),
                Health: health,
                HealthKind: healthKind,
                Site: DnacJson.First(row, "siteHierarchy", "location", "siteId"),
                Updated: DescribeTime(DnacJson.Long(row, "lastUpdatedTime", "lastUpdated", "timestamp"))));
        }

        return list;
    }

    /// <summary>有線か無線か。<b>知らない値はそのまま出す</b>。</summary>
    public static string DescribeConnection(string? type) => type switch
    {
        "WIRED" or "wired" => "有線",
        "WIRELESS" or "wireless" => "無線",
        null or "" => "—",
        _ => type,
    };

    /// <summary>周波数の言い方をそろえる。数字だけで返る版もある。</summary>
    public static string DescribeBand(string? band)
    {
        string text = (band ?? "").Trim();

        if (text.Length == 0) return "";
        if (text.Contains("2.4", StringComparison.Ordinal)) return "2.4GHz";
        if (text.StartsWith('6')) return "6GHz";
        if (text.StartsWith('5')) return "5GHz";

        return text;
    }

    /// <summary>
    /// 健全度。Catalyst Center のスコアは 1〜10 で、<b>8 以上が良好・4 未満は要確認</b>。
    /// 取れなければ「—」（0 点と混同させない）。
    /// </summary>
    public static (string Text, SeverityKind Kind) DescribeHealth(int? score) => score switch
    {
        null or < 1 => ("—", SeverityKind.Muted),
        >= 8 => ($"{score} ● 良好", SeverityKind.Ok),
        >= 4 => ($"{score} ⊘ 注意", SeverityKind.Notice),
        _ => ($"{score} ✕ 不良", SeverityKind.Alert),
    };

    // ===== 端末のイベント =====

    public static IReadOnlyList<DnacEventRow> ParseEvents(IEnumerable<JsonElement> rows)
    {
        var list = new List<DnacEventRow>();

        foreach (JsonElement row in rows)
        {
            string status = DnacJson.First(row, "eventStatus", "status", "resultStatus");
            (string statusText, SeverityKind statusKind) = DescribeEventStatus(status);

            list.Add(new DnacEventRow(
                Time: DescribeTime(DnacJson.Long(row, "timestamp", "eventTime", "startTime")),
                Name: DnacJson.First(row, "name", "eventName", "messageType", "subReasonType"),
                Status: statusText,
                StatusKind: statusKind,
                Source: Or(DnacJson.First(row, "apName", "networkDeviceName", "wlcName"),
                           DnacJson.First(row, "ssid")),
                Detail: Or(DnacJson.First(row, "details", "reasonType", "failureCategory"),
                           DnacJson.First(row, "additionalDetails"))));
        }

        return list;
    }

    /// <summary>
    /// イベントの成否。<b>失敗だけが見たいものなので、そこを目立たせる</b>。
    /// 知らない値はそのまま出す。
    /// </summary>
    public static (string Text, SeverityKind Kind) DescribeEventStatus(string? status)
    {
        if (string.IsNullOrEmpty(status)) return ("—", SeverityKind.Muted);

        if (status.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase)) return ("● 成功", SeverityKind.Ok);
        if (status.Contains("FAIL", StringComparison.OrdinalIgnoreCase)) return ("✕ 失敗", SeverityKind.Alert);
        if (status.Contains("WARN", StringComparison.OrdinalIgnoreCase)) return ("⊘ 警告", SeverityKind.Notice);

        return (status, SeverityKind.Muted);
    }

    /// <summary>
    /// イベントの API が無い版では、端末の「問題」を代わりに並べる。
    /// <b>空の表を出すより、持っているものを出す。</b>
    /// </summary>
    public static IReadOnlyList<DnacEventRow> ParseIssuesAsEvents(IEnumerable<JsonElement> rows)
    {
        var list = new List<DnacEventRow>();

        foreach (JsonElement row in rows)
        {
            IReadOnlyList<JsonElement> issues = DnacJson.Children(row, "issueDetails/issue");

            // enrichment は問題を入れ子で持つが、/issues は 1 行がそのまま 1 件。
            // 入れ子が無ければ行そのものを問題として読む（どちらの形でも表は同じ）
            foreach (JsonElement issue in issues.Count > 0 ? issues : [row])
            {
                list.Add(new DnacEventRow(
                    Time: DescribeTime(DnacJson.Long(
                        issue, "issueTimestamp", "timestamp", "last_occurence_time", "lastOccurenceTime")),
                    Name: DnacJson.First(
                        issue, "issueSummary", "issueCategory", "issueName", "name"),
                    Status: "⊘ 問題",
                    StatusKind: SeverityKind.Notice,
                    Source: DnacJson.First(
                        issue, "issueSource", "issueCategory", "deviceRole", "siteId"),
                    Detail: DnacJson.First(
                        issue, "issueDescription", "suggestedActions/message", "priority", "status")));
            }
        }

        return list;
    }

    // ===== 機器 =====

    /// <summary>
    /// 在庫と健全度を突き合わせる。<b>健全度が無い機器も落とさない</b>
    /// （在庫に居るのに一覧から消えると「その機器は無い」と誤読される）。
    /// 突き合わせの鍵は id → 無ければ管理 IP → 無ければシリアル。
    /// </summary>
    public static IReadOnlyList<DnacDeviceRow> ParseDevices(
        IEnumerable<JsonElement> inventory, IEnumerable<JsonElement>? health = null)
    {
        Dictionary<string, JsonElement> byKey = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement item in health ?? [])
        {
            foreach (string key in Keys(item))
                byKey.TryAdd(key, item);
        }

        var rows = new List<DnacDeviceRow>();

        foreach (JsonElement device in inventory)
        {
            JsonElement? scored = Keys(device).Select(k => byKey.TryGetValue(k, out JsonElement m) ? m : (JsonElement?)null)
                .FirstOrDefault(m => m is not null);

            int? score = DnacJson.Int(scored ?? device, HealthLeaf);
            (string health1, SeverityKind healthKind) = DescribeHealth(score);

            string reachability = DnacJson.First(device, "reachabilityStatus", "reachability", "collectionStatus");
            (string reachText, SeverityKind reachKind) = DescribeReachability(reachability);

            rows.Add(new DnacDeviceRow(
                // 表には出さないが、CLI を流すときの宛先はこの uuid で指す
                Id: DnacJson.First(device, "id", "instanceUuid", "deviceId"),
                Name: DnacJson.First(device, DeviceNameLeaf),
                Model: DnacJson.First(device, "platformId", "type", "series"),
                Serial: DnacJson.First(device, "serialNumber", "serial"),
                Version: DnacJson.First(device, "softwareVersion", "softwareType", "version"),
                Ip: DnacJson.First(device, "managementIpAddress", "managementIp", "ipAddress"),
                Site: DnacJson.First(device, "siteHierarchy", "location", "locationName"),
                Role: DnacJson.First(device, "role", "family"),
                Reachability: reachText,
                ReachabilityKind: reachKind,
                Health: health1,
                HealthKind: healthKind));
        }

        return rows;
    }

    /// <summary>到達性。<b>知らない値はそのまま出す</b>。</summary>
    public static (string Text, SeverityKind Kind) DescribeReachability(string? status) => status switch
    {
        "Reachable" or "REACHABLE" or "MANAGED" => ("● 到達", SeverityKind.Ok),
        "Unreachable" or "UNREACHABLE" => ("✕ 不達", SeverityKind.Alert),
        "Ping Reachable" => ("⊘ ping のみ", SeverityKind.Notice),
        null or "" => ("—", SeverityKind.Muted),
        _ => (status, SeverityKind.Muted),
    };

    private static IEnumerable<string> Keys(JsonElement item)
    {
        foreach (string key in (string[])
                 [
                     DnacJson.First(item, "id", "deviceId", "networkDeviceId", "instanceUuid"),
                     DnacJson.First(item, "managementIpAddress", "managementIp", "ipAddress"),
                     DnacJson.First(item, "serialNumber", "serial"),
                 ])
        {
            if (key.Length > 0) yield return key;
        }
    }

    // ===== 保守と適合（EoX / 適合性 / ライセンス） =====

    public static IReadOnlyList<DnacLifecycleRow> ParseEox(IEnumerable<JsonElement> rows)
    {
        var list = new List<DnacLifecycleRow>();

        foreach (JsonElement row in rows)
        {
            string scan = DnacJson.First(row, "scanStatus", "status");
            string date = FirstDate(row,
                "lastDateOfSupport", "endOfSupportDate", "endOfSaleDate",
                "eoxDetails/lastDateOfSupport", "eoxDetails/endOfSaleDate", "eoxDetails/endOfSupportDate");

            // 「対象なし」と「まだスキャンしていない」を混ぜない
            (string state, SeverityKind kind) = scan.Contains("NOT_SCANNED", StringComparison.OrdinalIgnoreCase)
                ? ("⊘ 未スキャン", SeverityKind.Notice)
                : date.Length > 0
                    ? ("⊘ 期日あり", SeverityKind.Notice)
                    : ("—", SeverityKind.Muted);

            list.Add(new DnacLifecycleRow(
                Device: Or(DnacJson.First(row, DeviceNameLeaf), DnacJson.First(row, "deviceId", "deviceUuid")),
                Kind: Or(DnacJson.First(row, "eoxDetails/eoxPhysicalType", "eoxPhysicalType"), "EoX"),
                State: state,
                StateKind: kind,
                Date: date,
                Note: Or(DnacJson.First(row, "eoxDetails/bulletinName", "bulletinName", "summary"), scan)));
        }

        return list;
    }

    public static IReadOnlyList<DnacLifecycleRow> ParseCompliance(IEnumerable<JsonElement> rows)
    {
        var list = new List<DnacLifecycleRow>();

        foreach (JsonElement row in rows)
        {
            string status = DnacJson.First(row, "complianceStatus", "status", "state");
            (string text, SeverityKind kind) = DescribeCompliance(status);

            list.Add(new DnacLifecycleRow(
                Device: Or(DnacJson.First(row, DeviceNameLeaf), DnacJson.First(row, "deviceUuid")),
                Kind: Or(DnacJson.First(row, "complianceType", "category"), "適合性"),
                State: text,
                StateKind: kind,
                Date: DescribeTime(DnacJson.Long(row, "lastSyncTime", "lastUpdateTime", "timestamp")),
                Note: DnacJson.First(row, "message", "displayName", "remediationSupported")));
        }

        return list;
    }

    public static (string Text, SeverityKind Kind) DescribeCompliance(string? status) => status switch
    {
        "COMPLIANT" or "Compliant" => ("● 適合", SeverityKind.Ok),
        "NON_COMPLIANT" or "NonCompliant" => ("✕ 不適合", SeverityKind.Alert),
        "IN_PROGRESS" or "REMEDIATION_IN_PROGRESS" => ("◌ 実行中", SeverityKind.Notice),
        "NOT_APPLICABLE" => ("— 対象外", SeverityKind.Muted),
        null or "" => ("—", SeverityKind.Muted),
        _ => (status, SeverityKind.Muted),
    };

    /// <summary>
    /// 脆弱性（PSIRT）。<b>1 件が「勧告 1 本」</b>で、機器ごとではなく対象台数で返る。
    ///
    /// 共通の 5 列には「機器＝対象台数 / 種別＝勧告の番号 / 状態＝重大度 / 日付＝直る版 /
    /// 備考＝CVE と参照先」で寄せる。<b>この表のために列を増やさない</b>（ほかの 3 種と同じ形で見る）。
    /// </summary>
    public static IReadOnlyList<DnacLifecycleRow> ParseAdvisories(IEnumerable<JsonElement> rows)
    {
        var list = new List<DnacLifecycleRow>();

        foreach (JsonElement row in rows)
        {
            (string text, SeverityKind kind) =
                DescribeAdvisorySeverity(DnacJson.First(row, "sir", "severity", "securityImpactRating"));

            string score = DnacJson.First(row, "cvssBaseScore", "cvssScore");
            string devices = DnacJson.First(row, "deviceCount", "totalDeviceCount");

            list.Add(new DnacLifecycleRow(
                Device: devices.Length > 0
                    ? $"対象 {devices} 台"
                    : DnacJson.First(row, "deviceHostName", "deviceId"),
                Kind: Or(DnacJson.First(row, "advisoryId", "id"), "脆弱性"),
                State: score.Length > 0 ? $"{text}　CVSS {score}" : text,
                StateKind: kind,
                Date: Join(DnacJson.Children(row, "fixedVersions", "firstFixedVersionsList")),
                Note: Or(Join(DnacJson.Children(row, "cves")),
                         DnacJson.First(row, "publicationUrl", "detailUrl"))));
        }

        return list;
    }

    /// <summary>勧告の重大度（Cisco の SIR）。<b>知らない値はそのまま出す</b>。</summary>
    public static (string Text, SeverityKind Kind) DescribeAdvisorySeverity(string? severity) => severity switch
    {
        "Critical" or "CRITICAL" => ("✕ 緊急", SeverityKind.Alert),
        "High" or "HIGH" => ("✕ 重大", SeverityKind.Alert),
        "Medium" or "MEDIUM" => ("⊘ 中", SeverityKind.Notice),
        "Low" or "LOW" or "Informational" or "INFORMATIONAL" => ("● 低", SeverityKind.Ok),
        null or "" => ("—", SeverityKind.Muted),
        _ => (severity, SeverityKind.Muted),
    };

    /// <summary>配列を読める 1 行にする。長いものは画面側で省く（全文は ToolTip）。</summary>
    private static string Join(IReadOnlyList<JsonElement> values)
        => string.Join(" / ", values
            .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            .Where(v => v.Length > 0));

    public static IReadOnlyList<DnacLifecycleRow> ParseLicenses(IEnumerable<JsonElement> rows)
    {
        var list = new List<DnacLifecycleRow>();

        foreach (JsonElement row in rows)
        {
            string status = DnacJson.First(row, "registration_status", "registrationStatus", "license_status");

            list.Add(new DnacLifecycleRow(
                Device: Or(DnacJson.First(row, DeviceNameLeaf),
                           DnacJson.First(row, "device_name", "device_uuid", "deviceUuid")),
                Kind: Or(DnacJson.First(row, "license_type", "licenseType", "dna_level"), "ライセンス"),
                State: status.Length > 0 ? status : "—",
                StateKind: status.Contains("REGISTERED", StringComparison.OrdinalIgnoreCase)
                    ? SeverityKind.Ok
                    : SeverityKind.Muted,
                Date: FirstDate(row, "license_expiry_date", "expiryDate", "evaluation_expiry_date"),
                Note: DnacJson.First(row, "virtual_account_name", "model", "device_type")));
        }

        return list;
    }

    // ===== CLI（参照のみ） =====



    /// <summary><b>語の切れ目で</b>見る（<c>shows</c> を <c>show</c> の仲間にしない）。</summary>
    private static bool StartsWithWord(string command, string word)
    {
        string head = word.Trim();

        if (head.Length == 0 || !command.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return false;

        return command.Length == head.Length || command[head.Length] == ' ';
    }


    // ===== 小物 =====

    /// <summary>ミリ秒のエポックを読める形に。0 や取れないものは空文字。</summary>
    public static string DescribeTime(long? epochMilliseconds)
    {
        if (epochMilliseconds is not { } value || value <= 0) return "";

        return DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string FirstDate(JsonElement row, params string[] paths)
    {
        string value = DnacJson.First(row, paths);

        if (value.Length == 0) return "";

        // 日付は文字列のことも、ミリ秒のエポックのこともある
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch)
            ? DescribeTime(epoch)
            : value;
    }

    private static JsonElement? Section(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement section)
           && section.ValueKind == JsonValueKind.Object
            ? section
            : null;

    private static string Or(string first, string second) => first.Length > 0 ? first : second;

    private static string Pick(JsonElement first, JsonElement second, params string[] paths)
        => Or(DnacJson.First(first, paths), DnacJson.First(second, paths));

    // ===== 応答コード =====

    /// <summary>
    /// 応答コードを日本語にする。<b>本文は読まない</b>（設定や端末の情報を画面やログに流さない）。
    /// </summary>
    public static string DescribeFailure(int statusCode) => statusCode switch
    {
        400 => "要求の内容が正しくありません（400）。入力した値を確認してください。",
        401 => "ログインできませんでした（401）。ユーザー名・パスワードと、"
               + "そのユーザーに参照の権限があるかを確認してください。",
        403 => "このユーザーでは参照できません（403）。",
        404 => "見つかりませんでした（404）。この版の Catalyst Center には無い機能かもしれません。",
        429 => "呼び出しが多すぎます（429）。しばらく待ってからもう一度取得してください。",
        >= 500 and < 600 => $"Catalyst Center 側で処理できませんでした（{statusCode}）。時間をおいて試してください。",
        _ => $"取得できませんでした（HTTP {statusCode}）。",
    };

    // ===== CSV / Excel =====

    public static CsvTable ToCsv(IReadOnlyList<DnacConnectionRow> rows) => new(
        ["MAC", "IP", "名前", "接続", "機器", "ポート／AP", "VLAN", "SSID", "帯域", "健全度", "サイト", "更新"],
        [.. rows.Select(r => new[]
        {
            r.Mac, r.Ip, r.HostName, r.Kind, r.Device, r.Port, r.Vlan, r.Ssid, r.Band, r.Health, r.Site, r.Updated,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<DnacEventRow> rows) => new(
        ["時刻", "種別", "結果", "発生元", "詳細"],
        [.. rows.Select(r => new[] { r.Time, r.Name, r.Status, r.Source, r.Detail })]);

    public static CsvTable ToCsv(IReadOnlyList<DnacDeviceRow> rows) => new(
        ["機器", "型番", "シリアル", "版", "IP", "サイト", "役割", "到達性", "健全度"],
        [.. rows.Select(r => new[]
        {
            r.Name, r.Model, r.Serial, r.Version, r.Ip, r.Site, r.Role, r.Reachability, r.Health,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<DnacLifecycleRow> rows) => new(
        ["機器", "種別", "状態", "日付", "備考"],
        [.. rows.Select(r => new[] { r.Device, r.Kind, r.State, r.Date, r.Note })]);
}
