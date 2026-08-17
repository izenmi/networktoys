using System.Globalization;
using System.Text.Json;
using NetworkToys.Core.Design;
using NetworkToys.Core.Metrics;
using NetworkToys.Core.Work;

namespace NetworkToys.Core.Wireless;

/// <summary>無線につながっている端末の 1 行。</summary>
public sealed record WlcClientRow(
    string Mac,
    string Ip,
    string Vendor,
    string ApName,
    string Ssid,
    string Radio,
    int Rssi,
    string RssiText,
    string Quality,
    string Snr,
    string Speed,
    string State,
    SeverityKind StateKind,
    string AssociatedAt);

/// <summary>AP の 1 行。<b>繋がっていない AP も同じ表に混ぜる</b>（別の表にしない）。</summary>
public sealed record WlcApRow(
    string Name,
    string State,
    SeverityKind StateKind,
    bool IsJoined,
    string Ip,
    string Mac,
    string Model,
    string Version,
    string Radios,
    int Clients,
    string ClientsText,
    string Tags);

/// <summary>AP の参加・切断の 1 行。WLC が持っている最終値だけ。</summary>
public sealed record WlcJoinRow(
    string Name,
    string Mac,
    string State,
    SeverityKind StateKind,
    string LastJoin,
    string LastDisconnect,
    string Reason,
    string Joins,
    string Failures);

/// <summary>電波の混み具合の 1 行（AP の無線 1 本ぶん）。</summary>
public sealed record WlcRrmRow(
    string ApName,
    string Radio,
    string Channel,
    string Power,
    int Utilization,
    string UtilizationText,
    SeverityKind UtilizationKind,
    string Noise,
    int Clients,
    string ClientsText);

/// <summary>不正 AP・隣接 AP の 1 行。</summary>
public sealed record WlcRogueRow(
    string Kind,
    string Bssid,
    string Vendor,
    string Ssid,
    string Channel,
    int Rssi,
    string RssiText,
    string DetectedBy,
    string LastHeard,
    string Note);

/// <summary>SSID(WLAN) ごとの内訳の 1 行。</summary>
public sealed record WlcSsidRow(
    string Ssid,
    string Profile,
    string Id,
    string State,
    SeverityKind StateKind,
    int Clients,
    int Band24,
    int Band5,
    int Band6);

/// <summary>
/// Catalyst 9800 の RESTCONF 応答を画面と CSV の行にする。
///
/// ここは HTTP に触らない（取ってくるのは App 側の <c>Services/WlcClient</c>）。
/// <b>リーフ名の候補はすべてこのファイルの上の方に並べてある。</b>
/// 実機で外れたら、そこへ 1 行足すだけで直るようにしてある。
/// </summary>
public static class WlcCatalog
{
    // ===== 問い合わせ先（版でモジュール名が割れるものは候補を並べる。順に試す） =====

    private const string ClientOper = "/restconf/data/Cisco-IOS-XE-wireless-client-oper:client-oper-data";
    private const string ApOper = "/restconf/data/Cisco-IOS-XE-wireless-access-point-oper:access-point-oper-data";

    public static string[] ClientCommonPaths => [$"{ClientOper}/common-oper-data"];
    public static string[] ClientDot11Paths => [$"{ClientOper}/dot11-oper-data"];
    public static string[] ClientTrafficPaths => [$"{ClientOper}/traffic-stats"];

    /// <summary>IP と MAC の対応。<b>IP から端末を探すにはこれが要る</b>（逆索引は WLC に無い）。</summary>
    public static string[] ClientSisfPaths => [$"{ClientOper}/sisf-db-mac"];

    public static string[] ApCapwapPaths => [$"{ApOper}/capwap-data"];
    public static string[] ApRadioPaths => [$"{ApOper}/radio-oper-data"];

    /// <summary>設定として登録されている AP（タグを当てたものだけ出る）。</summary>
    public static string[] ApTagPaths => ["/restconf/data/Cisco-IOS-XE-wireless-ap-cfg:ap-cfg-data/ap-tags/ap-tag"];

    public static string[] ApJoinPaths =>
    [
        "/restconf/data/Cisco-IOS-XE-wireless-ap-global-oper:ap-global-oper-data/ap-join-stats",
        $"{ApOper}/ap-join-stats",
    ];

    public static string[] RrmPaths =>
    [
        "/restconf/data/Cisco-IOS-XE-wireless-rrm-oper:rrm-oper-data/rrm-measurement",
        "/restconf/data/Cisco-IOS-XE-wireless-rrm-global-oper:rrm-global-oper-data/rrm-measurement",
    ];

    public static string[] RoguePaths =>
    [
        "/restconf/data/Cisco-IOS-XE-wireless-rogue-oper:rogue-oper-data/rogue-data",
        "/restconf/data/Cisco-IOS-XE-wireless-rogue-oper:rogue-data",
    ];

    public static string[] NeighborPaths => [$"{ApOper}/ap-radio-neighbor"];

    public static string[] WlanPaths =>
        ["/restconf/data/Cisco-IOS-XE-wireless-wlan-cfg:wlan-cfg-data/wlan-cfg-entries/wlan-cfg-entry"];

    // ===== リーフ名の候補（版で動く。ここに 1 行足せば直る） =====

    private static readonly string[] ClientMac = ["client-mac", "ms-mac-address", "mac-addr", "mac-address"];
    private static readonly string[] ApNameLeaf = ["ap-name", "ap-join-info/ap-name", "wtp-name", "name"];
    private static readonly string[] ApMacLeaf = ["wtp-mac", "ap-mac", "mac", "ap-mac-address"];
    private static readonly string[] LastJoinLeaf =
        ["last-successful-join-time", "ap-join-info/last-successful-join-time", "join-time", "last-join-time"];
    private static readonly string[] LastDisconnectLeaf =
        ["last-disconnect-time", "ap-disconnect-detail/disconnect-time", "disconnect-time", "last-disconnect"];
    private static readonly string[] DisconnectReasonLeaf =
        ["last-disconnect-reason", "ap-disconnect-detail/disconnect-reason-str", "disconnect-reason", "disconnect-reason-str"];

    // ===== クライアント =====

    /// <summary>
    /// 4 つの一覧を MAC で 1 行にまとめる。
    ///
    /// <b>IP は <c>sisf-db-mac</c> にしか無い。</b>そこに載っていない端末は IP を空にする
    /// （「まだ IP を学習していない」を 0.0.0.0 などで埋めない）。
    /// </summary>
    public static IReadOnlyList<WlcClientRow> ParseClients(
        IEnumerable<JsonElement> common,
        IEnumerable<JsonElement> dot11,
        IEnumerable<JsonElement> traffic,
        IEnumerable<JsonElement> sisf,
        Func<string, string?>? vendorOf = null)
    {
        Dictionary<string, JsonElement> byDot11 = ByMac(dot11);
        Dictionary<string, JsonElement> byTraffic = ByMac(traffic);
        Dictionary<string, string> ipByMac = IpByMac(sisf);

        var rows = new List<WlcClientRow>();

        foreach (JsonElement client in common)
        {
            string mac = WlcYang.First(client, ClientMac);
            string key = NormalizeMac(mac);

            byDot11.TryGetValue(key, out JsonElement radio);
            byTraffic.TryGetValue(key, out JsonElement stats);

            int rssi = WlcYang.Int(stats, "most-recent-rssi", "rssi") ?? 0;
            string channel = WlcYang.First(radio, "current-channel", "channel", "ms-channel");
            string radioType = WlcYang.First(radio, "radio-type", "ms-radio-type", "ms-wifi-type");
            string slot = WlcYang.First(client, "ms-ap-slot-id", "slot-id");
            string state = WlcYang.First(client, "co-state", "client-state", "ms-client-state");

            (string stateText, SeverityKind stateKind) = DescribeClientState(state);

            rows.Add(new WlcClientRow(
                Mac: mac,
                Ip: ipByMac.TryGetValue(key, out string? ip) ? ip : "",
                Vendor: vendorOf?.Invoke(mac) ?? "",
                ApName: WlcYang.First(client, ApNameLeaf),
                Ssid: WlcYang.First(radio, "vap-ssid", "ssid", "ms-ssid"),
                Radio: DescribeRadio(radioType, slot, channel),
                Rssi: rssi,
                RssiText: rssi == 0 ? "—" : rssi.ToString(CultureInfo.InvariantCulture),
                Quality: rssi == 0 ? "—" : WifiSignalGuide.Describe(rssi),
                Snr: WlcYang.First(stats, "most-recent-snr", "snr"),
                Speed: WlcYang.First(stats, "speed", "current-rate", "data-rate"),
                State: stateText,
                StateKind: stateKind,
                AssociatedAt: WlcYang.First(radio, "ms-assoc-time", "assoc-time", "association-time")));
        }

        return rows;
    }

    /// <summary>
    /// 入力が IP でも MAC でも名前でも、同じ欄で探せるようにする。
    /// MAC は <c>aabb.ccdd.eeff</c> / <c>AA-BB-…</c> / <c>aa:bb:…</c> のどれで打たれても当てる。
    /// </summary>
    public static IReadOnlyList<WlcClientRow> FilterClients(IEnumerable<WlcClientRow> rows, string? query)
    {
        string text = (query ?? "").Trim();

        if (text.Length == 0) return [.. rows];

        string mac = NormalizeMac(text);
        bool looksLikeMac = mac.Length >= 4 && mac.All(Uri.IsHexDigit);

        return
        [
            .. rows.Where(r =>
                (looksLikeMac && NormalizeMac(r.Mac).Contains(mac, StringComparison.Ordinal))
                || r.Ip.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.Mac.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.ApName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.Ssid.Contains(text, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    private static Dictionary<string, JsonElement> ByMac(IEnumerable<JsonElement> rows)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (JsonElement row in rows)
        {
            string key = NormalizeMac(WlcYang.First(row, ClientMac));

            if (key.Length > 0) map[key] = row;
        }

        return map;
    }

    /// <summary>MAC → IP。IPv4 が無ければ IPv6 を使う。</summary>
    private static Dictionary<string, string> IpByMac(IEnumerable<JsonElement> sisf)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (JsonElement row in sisf)
        {
            string key = NormalizeMac(WlcYang.First(row, ClientMac));
            if (key.Length == 0) continue;

            string ip = WlcYang.First(row,
                "ipv4-binding/ip-key/ip-addr", "ipv4-binding/ip-addr", "ip-addr",
                "ipv6-binding/ipv6-key/ip-addr", "ipv6-binding/ip-addr");

            if (ip.Length > 0 && ip != "0.0.0.0") map[key] = ip;
        }

        return map;
    }

    // ===== AP =====

    /// <summary>
    /// AP の一覧。<b>繋がっていない AP をここに混ぜる</b>のがこの画面の肝。
    ///
    /// <b>MAC が 2 種類あることに注意。</b>設定側(<c>ap-tag</c>)の <c>ap-mac</c> は
    /// <b>Ethernet の MAC</b>、稼働側(<c>capwap-data</c>)の <c>wtp-mac</c> は
    /// <b>無線のベース MAC</b> で、値が違う。素直に突き合わせると
    /// <b>全 AP が「未接続」になる</b>ので、稼働側は両方の MAC を鍵にして持つ。
    ///
    /// また <c>ap-tag</c> には<b>タグを当てた AP しか出ない</b>ので、
    /// 参加記録(<c>ap-join-stats</c>)との和を「WLC が知っている AP」とみなす。
    /// </summary>
    public static IReadOnlyList<WlcApRow> ParseAps(
        IEnumerable<JsonElement> capwap,
        IEnumerable<JsonElement> radios,
        IEnumerable<JsonElement> apTags,
        IEnumerable<JsonElement> joinStats,
        IReadOnlyDictionary<string, int>? clientsByAp = null)
    {
        Dictionary<string, List<JsonElement>> radioByMac = Group(radios, r => NormalizeMac(WlcYang.First(r, ApMacLeaf)));

        var rows = new List<WlcApRow>();
        var joined = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (JsonElement ap in capwap)
        {
            string radioMac = WlcYang.First(ap, ApMacLeaf);
            string ethMac = WlcYang.First(ap,
                "device-detail/static-info/board-data/wtp-enet-mac", "eth-mac", "ethernet-mac");

            // 2 つの MAC はどちらも「この AP」の鍵になる
            foreach (string key in (string[])[NormalizeMac(radioMac), NormalizeMac(ethMac)])
            {
                if (key.Length > 0) joined.Add(key);
            }

            string name = WlcYang.First(ap, ApNameLeaf);
            int clients = clientsByAp is not null && clientsByAp.TryGetValue(name, out int count) ? count : -1;

            rows.Add(new WlcApRow(
                Name: name.Length > 0 ? name : radioMac,
                State: "● 接続中",
                StateKind: SeverityKind.Ok,
                IsJoined: true,
                Ip: WlcYang.First(ap, "ip-addr", "device-detail/static-info/ap-ip-addr", "ap-ip-address"),
                Mac: radioMac,
                Model: WlcYang.First(ap,
                    "device-detail/static-info/ap-models/model", "device-detail/static-info/model", "model"),
                Version: WlcYang.First(ap,
                    "device-detail/wtp-version/sw-version", "device-detail/static-info/ap-version",
                    "sw-version", "version"),
                Radios: DescribeRadios(radioByMac, radioMac),
                Clients: clients,
                ClientsText: clients < 0 ? "—" : clients.ToString(CultureInfo.InvariantCulture),
                Tags: DescribeTags(ap)));
        }

        // 設定と参加記録の両方から「WLC が知っている AP」を集める
        foreach (JsonElement known in apTags.Concat(joinStats))
        {
            string mac = WlcYang.First(known, ApMacLeaf);
            string key = NormalizeMac(mac);

            if (key.Length == 0 || joined.Contains(key)) continue;
            if (!names.TryAdd(key, WlcYang.First(known, ApNameLeaf))) continue;

            string name = names[key];

            rows.Add(new WlcApRow(
                Name: name.Length > 0 ? name : mac,
                State: "✕ 未接続",
                StateKind: SeverityKind.Alert,
                IsJoined: false,
                Ip: "",
                Mac: mac,
                Model: "",
                Version: "",
                Radios: "",
                Clients: -1,
                ClientsText: "—",
                Tags: DescribeTags(known)));
        }

        return rows;
    }

    /// <summary>AP 名ごとの接続台数。AP 一覧の「台数」に使う。</summary>
    public static IReadOnlyDictionary<string, int> CountClientsByAp(IEnumerable<WlcClientRow> clients)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (WlcClientRow client in clients)
        {
            if (client.ApName.Length == 0) continue;

            counts[client.ApName] = counts.GetValueOrDefault(client.ApName) + 1;
        }

        return counts;
    }

    private static string DescribeRadios(Dictionary<string, List<JsonElement>> byMac, string mac)
    {
        if (!byMac.TryGetValue(NormalizeMac(mac), out List<JsonElement>? radios)) return "";

        var parts = new List<string>();

        foreach (JsonElement radio in radios)
        {
            string slot = WlcYang.First(radio, "radio-slot-id", "slot-id");
            string channel = WlcYang.First(radio,
                "phy-ht-cfg/cfg-data/curr-freq", "radio-band-info/phy-ht-cfg/cfg-data/curr-freq", "curr-freq");
            string band = DescribeRadio(WlcYang.First(radio, "radio-type"), slot, channel);
            string oper = WlcYang.First(radio, "oper-state", "radio-oper-state", "admin-state");

            parts.Add($"{band} {(oper.Contains("up", StringComparison.OrdinalIgnoreCase) ? "●" : "✕")}".Trim());
        }

        return string.Join(" / ", parts);
    }

    private static string DescribeTags(JsonElement node)
    {
        string[] tags =
        [
            WlcYang.First(node, "tag-info/policy-tag-info/policy-tag-name", "policy-tag"),
            WlcYang.First(node, "tag-info/site-tag/site-tag-name", "site-tag"),
            WlcYang.First(node, "tag-info/rf-tag/rf-tag-name", "rf-tag"),
        ];

        return string.Join(" / ", tags.Where(t => t.Length > 0));
    }

    // ===== 参加・切断 =====

    /// <summary>
    /// WLC が持っている最終参加・最終切断。<b>履歴はここまでしか無い</b>
    /// （何度も落ちている AP でも、分かるのは直近の 1 回とその回数）。
    /// </summary>
    public static IReadOnlyList<WlcJoinRow> ParseJoins(
        IEnumerable<JsonElement> joinStats, IEnumerable<WlcApRow> aps)
    {
        HashSet<string> joined =
        [
            .. aps.Where(a => a.IsJoined).Select(a => NormalizeMac(a.Mac)).Where(m => m.Length > 0),
        ];

        var rows = new List<WlcJoinRow>();

        foreach (JsonElement stat in joinStats)
        {
            string mac = WlcYang.First(stat, ApMacLeaf);
            bool isJoined = joined.Contains(NormalizeMac(mac));

            rows.Add(new WlcJoinRow(
                Name: WlcYang.First(stat, ApNameLeaf) is { Length: > 0 } name ? name : mac,
                Mac: mac,
                State: isJoined ? "● 接続中" : "✕ 未接続",
                StateKind: isJoined ? SeverityKind.Ok : SeverityKind.Alert,
                LastJoin: WlcYang.First(stat, LastJoinLeaf),
                LastDisconnect: WlcYang.First(stat, LastDisconnectLeaf),
                Reason: WlcYang.First(stat, DisconnectReasonLeaf),
                Joins: WlcYang.First(stat, "num-successful-joins", "ap-join-info/num-successful-joins", "join-count"),
                Failures: WlcYang.First(stat, "num-unsuccessful-joins", "ap-join-info/num-unsuccessful-joins")));
        }

        return rows;
    }

    // ===== 電波（RRM） =====

    public static IReadOnlyList<WlcRrmRow> ParseRrm(
        IEnumerable<JsonElement> measurements, IEnumerable<WlcApRow> aps)
    {
        Dictionary<string, string> nameByMac = aps
            .Where(a => a.Mac.Length > 0)
            .GroupBy(a => NormalizeMac(a.Mac), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var rows = new List<WlcRrmRow>();

        foreach (JsonElement measurement in measurements)
        {
            string mac = WlcYang.First(measurement, ApMacLeaf);
            string slot = WlcYang.First(measurement, "radio-slot-id", "slot-id");
            string channel = WlcYang.First(measurement, "load/current-channel", "current-channel", "channel");

            int util = WlcYang.Int(measurement,
                "load/cca-util-percentage", "load/channel-utilization", "cca-util-percentage") ?? -1;

            (string utilText, SeverityKind utilKind) = DescribeUtilization(util);
            int clients = WlcYang.Int(measurement, "load/stations", "stations", "client-count") ?? -1;

            rows.Add(new WlcRrmRow(
                ApName: nameByMac.TryGetValue(NormalizeMac(mac), out string? name) ? name : mac,
                Radio: DescribeRadio(WlcYang.First(measurement, "radio-type"), slot, channel),
                Channel: channel,
                Power: WlcYang.First(measurement, "tx-power", "load/tx-power-level", "power-level"),
                Utilization: util,
                UtilizationText: utilText,
                UtilizationKind: utilKind,
                Noise: WlcYang.First(measurement, "noise/noise-data/noise", "noise/noise", "noise"),
                Clients: clients,
                ClientsText: clients < 0 ? "—" : clients.ToString(CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    /// <summary>
    /// チャンネルの使用率。<b>50% を超えたあたりから体感に出る</b>ので、そこで色を変える。
    /// </summary>
    public static (string Text, SeverityKind Kind) DescribeUtilization(int percent) => percent switch
    {
        < 0 => ("—", SeverityKind.Muted),
        >= 70 => ($"{percent}%", SeverityKind.Alert),
        >= 50 => ($"{percent}%", SeverityKind.Notice),
        _ => ($"{percent}%", SeverityKind.Ok),
    };

    // ===== 不正 AP・隣接 AP =====

    public static IReadOnlyList<WlcRogueRow> ParseRogues(
        IEnumerable<JsonElement> rogues,
        IEnumerable<JsonElement> neighbors,
        Func<string, string?>? vendorOf = null)
    {
        var rows = new List<WlcRogueRow>();

        foreach (JsonElement rogue in rogues)
        {
            string bssid = WlcYang.First(rogue, "rogue-address", "bssid", "mac-address");
            int rssi = WlcYang.Int(rogue, "rogue-radio/rssi", "rssi", "last-heard-rssi") ?? 0;

            rows.Add(new WlcRogueRow(
                Kind: "不正",
                Bssid: bssid,
                Vendor: vendorOf?.Invoke(bssid) ?? "",
                Ssid: WlcYang.First(rogue, "rogue-ssid", "ssid"),
                Channel: WlcYang.First(rogue, "rogue-radio/channel", "channel"),
                Rssi: rssi,
                RssiText: rssi == 0 ? "—" : rssi.ToString(CultureInfo.InvariantCulture),
                DetectedBy: WlcYang.First(rogue, "rogue-radio/reported-ap-name", "detecting-ap-name", "ap-name"),
                LastHeard: WlcYang.First(rogue, "last-heard", "rogue-last-heard", "last-heard-time"),
                Note: DescribeRogueClass(WlcYang.First(rogue, "rogue-class-type", "rogue-classification", "class-type"))));
        }

        foreach (JsonElement neighbor in neighbors)
        {
            string bssid = WlcYang.First(neighbor, "bssid", "neighbor-bssid", "mac-address");
            int rssi = WlcYang.Int(neighbor, "rssi", "neighbor-rssi") ?? 0;

            rows.Add(new WlcRogueRow(
                Kind: "隣接",
                Bssid: bssid,
                Vendor: vendorOf?.Invoke(bssid) ?? "",
                Ssid: WlcYang.First(neighbor, "ssid", "neighbor-ssid"),
                Channel: WlcYang.First(neighbor, "channel", "neighbor-channel"),
                Rssi: rssi,
                RssiText: rssi == 0 ? "—" : rssi.ToString(CultureInfo.InvariantCulture),
                DetectedBy: WlcYang.First(neighbor, "ap-name", "ap-mac"),
                LastHeard: WlcYang.First(neighbor, "last-update-rcvd", "last-updated"),
                Note: ""));
        }

        return rows;
    }

    /// <summary>不正 AP の分類。<b>知らない値はそのまま出す</b>。</summary>
    public static string DescribeRogueClass(string? classType) => classType switch
    {
        "malicious" or "rogue-classtype-malicious" => "悪意あり",
        "friendly" or "rogue-classtype-friendly" => "既知",
        "unclassified" or "rogue-classtype-unclassified" => "未分類",
        "custom" or "rogue-classtype-custom" => "独自分類",
        null or "" => "",
        _ => classType,
    };

    // ===== SSID =====

    /// <summary>
    /// WLAN の一覧に、いま繋がっている台数と帯域別の内訳を添える。
    /// 台数はクライアント一覧から数える（WLC 側に「SSID ごとの台数」は無い）。
    /// </summary>
    public static IReadOnlyList<WlcSsidRow> ParseSsids(
        IEnumerable<JsonElement> wlans, IEnumerable<WlcClientRow> clients)
    {
        List<WlcClientRow> all = [.. clients];
        var rows = new List<WlcSsidRow>();

        foreach (JsonElement wlan in wlans)
        {
            string ssid = WlcYang.First(wlan, "apf-vap-id-data/ssid", "ssid", "wlan-ssid");
            string status = WlcYang.First(wlan, "apf-vap-id-data/wlan-status", "wlan-status", "enabled");

            List<WlcClientRow> mine = [.. all.Where(c => string.Equals(c.Ssid, ssid, StringComparison.OrdinalIgnoreCase))];
            (string stateText, SeverityKind stateKind) = DescribeWlanStatus(status);

            rows.Add(new WlcSsidRow(
                Ssid: ssid,
                Profile: WlcYang.First(wlan, "profile-name", "wlan-profile-name", "name"),
                Id: WlcYang.First(wlan, "wlan-id", "id"),
                State: stateText,
                StateKind: stateKind,
                Clients: mine.Count,
                Band24: mine.Count(c => c.Radio.StartsWith("2.4", StringComparison.Ordinal)),
                Band5: mine.Count(c => c.Radio.StartsWith("5", StringComparison.Ordinal)),
                Band6: mine.Count(c => c.Radio.StartsWith("6", StringComparison.Ordinal))));
        }

        return rows;
    }

    public static (string Text, SeverityKind Kind) DescribeWlanStatus(string? status) => status switch
    {
        "true" or "enabled" or "up" => ("● 有効", SeverityKind.Ok),
        "false" or "disabled" or "down" => ("✕ 無効", SeverityKind.Muted),
        null or "" => ("—", SeverityKind.Muted),
        _ => (status, SeverityKind.Muted),
    };

    // ===== 文字起こし =====

    public static (string Text, SeverityKind Kind) DescribeClientState(string? state)
    {
        if (string.IsNullOrEmpty(state)) return ("—", SeverityKind.Muted);

        // 版によって "client-status-run" だったり "run" だったりする
        if (state.Contains("run", StringComparison.OrdinalIgnoreCase)) return ("● 通信中", SeverityKind.Ok);
        if (state.Contains("auth", StringComparison.OrdinalIgnoreCase)) return ("◌ 認証中", SeverityKind.Notice);
        if (state.Contains("delete", StringComparison.OrdinalIgnoreCase)) return ("✕ 切断中", SeverityKind.Muted);

        return (state, SeverityKind.Muted);
    }

    /// <summary>
    /// 帯域とチャンネルの表示。無線の種別 → スロット番号 → チャンネル番号の順に見る
    /// （どれか 1 つは取れる。<b>取れなければ捏造しない</b>）。
    /// </summary>
    public static string DescribeRadio(string? radioType, string? slot, string? channel)
    {
        string band = BandOf(radioType, slot, channel);
        string ch = (channel ?? "").Trim();

        if (band.Length == 0) return ch.Length > 0 ? $"ch{ch}" : "";

        return ch.Length > 0 ? $"{band} ch{ch}" : band;
    }

    /// <summary>帯域だけ。「2.4GHz」「5GHz」「6GHz」か、分からなければ空文字。</summary>
    public static string BandOf(string? radioType, string? slot, string? channel)
    {
        string type = (radioType ?? "").ToLowerInvariant();

        if (type.Length > 0)
        {
            if (type.Contains("6ghz", StringComparison.Ordinal) || type.Contains("6-ghz", StringComparison.Ordinal))
                return "6GHz";

            if (type.Contains("2.4", StringComparison.Ordinal) || type.Contains("2-4", StringComparison.Ordinal)
                || type.Contains("bg", StringComparison.Ordinal) || type.Contains("11b", StringComparison.Ordinal))
                return "2.4GHz";

            if (type.Contains("5ghz", StringComparison.Ordinal) || type.Contains("5-ghz", StringComparison.Ordinal))
                return "5GHz";
        }

        // スロットは 0=2.4 / 1=5 / 2=6 が通例
        if (int.TryParse(slot, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slotId))
        {
            if (slotId == 0) return "2.4GHz";
            if (slotId == 1) return "5GHz";
            if (slotId == 2) return "6GHz";
        }

        // 最後の頼みはチャンネル番号（6GHz は 5GHz と番号が重なるので当てられない）
        if (int.TryParse(channel, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch))
            return ch is > 0 and <= 14 ? "2.4GHz" : "5GHz";

        return "";
    }

    /// <summary>比べるための MAC。<b>表示は機器が返した形のまま</b>で、比較にだけこれを使う。</summary>
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

    private static Dictionary<string, List<JsonElement>> Group(
        IEnumerable<JsonElement> rows, Func<JsonElement, string> key)
    {
        var map = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);

        foreach (JsonElement row in rows)
        {
            string k = key(row);
            if (k.Length == 0) continue;

            if (!map.TryGetValue(k, out List<JsonElement>? list))
            {
                list = [];
                map[k] = list;
            }

            list.Add(row);
        }

        return map;
    }

    // ===== 応答コード =====

    /// <summary>
    /// 応答コードを日本語にする。<b>本文は読まない</b>（設定の断片を画面やログに流さない）。
    /// RESTCONF ならではの 2 つ（204 と 406）を必ず書き分ける。
    /// </summary>
    public static string DescribeFailure(int statusCode) => statusCode switch
    {
        400 => "要求の内容が正しくありません（400）。",
        401 => "ログインできませんでした（401）。ユーザー名・パスワードと、"
               + "そのユーザーに読み取りの権限（特権 15）があるかを確認してください。",
        403 => "このユーザーでは参照できません（403）。",
        404 => "見つかりませんでした（404）。この WLC では RESTCONF が無効か、"
               + "版にこのデータが無い可能性があります。方式を SSH にして試してください。",
        405 => "その操作は許可されていません（405）。",
        406 => "応答の形式を受け取れませんでした（406）。RESTCONF の応答形式を確認してください。",
        >= 500 and < 600 => $"WLC 側で処理できませんでした（{statusCode}）。時間をおいて試してください。",
        _ => $"取得できませんでした（HTTP {statusCode}）。",
    };

    // ===== CSV / Excel =====

    public static CsvTable ToCsv(IReadOnlyList<WlcClientRow> rows) => new(
        ["MAC", "IP", "メーカー", "AP", "SSID", "電波", "RSSI", "品質", "SNR", "速度", "状態", "接続時刻"],
        [.. rows.Select(r => new[]
        {
            r.Mac, r.Ip, r.Vendor, r.ApName, r.Ssid, r.Radio, r.RssiText, r.Quality,
            r.Snr, r.Speed, r.State, r.AssociatedAt,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<WlcApRow> rows) => new(
        ["状態", "AP", "IP", "MAC", "型番", "版", "無線", "台数", "タグ"],
        [.. rows.Select(r => new[]
        {
            r.State, r.Name, r.Ip, r.Mac, r.Model, r.Version, r.Radios, r.ClientsText, r.Tags,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<WlcJoinRow> rows) => new(
        ["状態", "AP", "MAC", "最終参加", "最終切断", "切断理由", "参加", "失敗"],
        [.. rows.Select(r => new[]
        {
            r.State, r.Name, r.Mac, r.LastJoin, r.LastDisconnect, r.Reason, r.Joins, r.Failures,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<WlcRrmRow> rows) => new(
        ["AP", "無線", "チャンネル", "出力", "使用率", "雑音", "台数"],
        [.. rows.Select(r => new[]
        {
            r.ApName, r.Radio, r.Channel, r.Power, r.UtilizationText, r.Noise, r.ClientsText,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<WlcRogueRow> rows) => new(
        ["種別", "BSSID", "メーカー", "SSID", "チャンネル", "電波強度", "検知した AP", "最終受信", "備考"],
        [.. rows.Select(r => new[]
        {
            r.Kind, r.Bssid, r.Vendor, r.Ssid, r.Channel, r.RssiText, r.DetectedBy, r.LastHeard, r.Note,
        })]);

    public static CsvTable ToCsv(IReadOnlyList<WlcSsidRow> rows) => new(
        ["SSID", "プロファイル", "ID", "状態", "台数", "2.4GHz", "5GHz", "6GHz"],
        [.. rows.Select(r => new[]
        {
            r.Ssid, r.Profile, r.Id, r.State,
            r.Clients.ToString(CultureInfo.InvariantCulture),
            r.Band24.ToString(CultureInfo.InvariantCulture),
            r.Band5.ToString(CultureInfo.InvariantCulture),
            r.Band6.ToString(CultureInfo.InvariantCulture),
        })]);
}
