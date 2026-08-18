using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NetworkToys.Core.Addressing;
using NetworkToys.Core.Design;
using NetworkToys.Core.Net;
using NetworkToys.Core.Work;

namespace NetworkToys.Core.Cloud;

/// <summary>ネットワーク一覧の 1 行。</summary>
public sealed record MerakiNetworkRow(
    string Name,
    string Id,
    string ProductTypes,
    string TimeZone,
    string Tags);

/// <summary>機器一覧の 1 行。機器情報と稼働状況を突き合わせた結果。</summary>
public sealed record MerakiDeviceRow(
    string Name,
    string Model,
    string Serial,
    string Firmware,
    string Network,
    string State,
    ConnectionStateKind StateKind,
    string LanIp);

/// <summary>アップリンクの 1 行。MX 1 台につき WAN1/WAN2 で 2 行になる。</summary>
/// <param name="RawStatus">
/// 応答の <c>status</c> をそのまま。導入時確認は<b>「待機」と「接続中」を区別する</b>必要があり、
/// 表示用の <c>State</c> ではどちらも「◌」で見分けが付かない。
/// </param>
public sealed record MerakiUplinkRow(
    string Network,
    string Serial,
    string Interface,
    string State,
    ConnectionStateKind StateKind,
    string Ip,
    string Gateway,
    string PublicIp,
    string RawStatus = "");

/// <summary>クライアント一覧の 1 行。</summary>
public sealed record MerakiClientRow(
    string Network,
    string Description,
    string Ip,
    string Mac,
    string Vlan,
    string Manufacturer,
    string Usage,
    string LastSeen);

/// <summary>拠点ごとの内訳の 1 行。クライアント数は期間つきで数えたもの。</summary>
public sealed record MerakiSiteRow(
    string Network,
    string NetworkId,
    int Clients,
    string ClientsText,
    string Segments,
    string Note);

/// <summary>DHCP の払い出し状況の 1 行（MX 1 台の 1 サブネット）。</summary>
public sealed record MerakiDhcpRow(
    string Network,
    string Device,
    string Vlan,
    string Subnet,
    int Used,
    string UsedText,
    int Free,
    string FreeText,
    int UsagePercent,
    string UsageText,
    SeverityKind UsageKind);

/// <summary>
/// 拠点ごとの通信量の 1 行（回線 1 本ぶん）。<b>期間内に流れた量</b>で、毎秒に直したものではない。
/// </summary>
/// <param name="Kilobytes">並べ替えと棒の長さに使う生の値（応答の単位のまま）。</param>
public sealed record MerakiTrafficRow(
    string Network,
    double Kilobytes,
    string Total,
    string Sent,
    string Received);

/// <summary>アラートの 1 行。</summary>
public sealed record MerakiAlertRow(
    string Severity,
    SeverityKind SeverityKind,
    string Type,
    string Network,
    string Device,
    string StartedAt,
    string Detail);

/// <summary>
/// Meraki ダッシュボード API の応答を画面と CSV の行に変換する。
///
/// ここは HTTP に触らない。ページを取ってくるのは App 側の
/// <c>Services/MerakiDashboard</c> で、このクラスは受け取った JSON 文字列を
/// 行にするだけなので、固定のサンプルでそのまま検証できる。
///
/// 型付きの POCO は作らず <see cref="JsonDocument"/> で必要な項目だけ拾う。
/// Meraki は項目が増減するうえ、clients の vlan のように
/// 数値と文字列が混在する項目があり、型を決め打ちすると
/// 一覧が丸ごと落ちる（1 台の想定外で全台見えなくなる方が困る）。
/// </summary>
public static class MerakiCatalog
{
    // ===== 組織・ネットワーク =====

    public static IReadOnlyList<(string Id, string Name)> ParseOrganizations(IEnumerable<string> pages)
    {
        var list = new List<(string, string)>();

        foreach (JsonElement item in Items(pages))
            list.Add((Str(item, "id"), Str(item, "name")));

        return list;
    }

    public static IReadOnlyList<MerakiNetworkRow> ParseNetworks(IEnumerable<string> pages)
    {
        var rows = new List<MerakiNetworkRow>();

        foreach (JsonElement item in Items(pages))
        {
            rows.Add(new MerakiNetworkRow(
                Name: Str(item, "name"),
                Id: Str(item, "id"),
                ProductTypes: Join(item, "productTypes"),
                TimeZone: Str(item, "timeZone"),
                Tags: Join(item, "tags")));
        }

        return rows;
    }

    // ===== 機器 =====

    /// <summary>
    /// 機器一覧（型番・ファーム）と稼働状況（状態・グローバル IP）をシリアルで突き合わせる。
    /// 片方にしか出てこないシリアルも落とさない。1 項目の欠落で機器が一覧から消えると、
    /// 「その機器が無い」のか「情報が取れなかった」のか区別できなくなる。
    /// </summary>
    /// <param name="runningVersions">
    /// シリアル → いま動いている版（<see cref="RunningVersions"/>）。
    /// 設定と違う版で動いている機器の <c>firmware</c> は版ではなく英文なので、ここから補う。
    /// </param>
    public static IReadOnlyList<MerakiDeviceRow> JoinDevices(
        IEnumerable<string> devicePages,
        IEnumerable<string> statusPages,
        IReadOnlyList<MerakiNetworkRow> networks,
        IReadOnlyDictionary<string, string>? runningVersions = null)
    {
        Dictionary<string, string> networkNames = networks
            .GroupBy(n => n.Id)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

        var byStatus = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in Items(statusPages))
        {
            string serial = Str(item, "serial");
            if (serial.Length > 0)
                byStatus[serial] = item;
        }

        var rows = new List<MerakiDeviceRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement item in Items(devicePages))
        {
            string serial = Str(item, "serial");
            seen.Add(serial);

            byStatus.TryGetValue(serial, out JsonElement status);
            rows.Add(BuildDeviceRow(item, status, networkNames, runningVersions));
        }

        // 機器一覧に出てこなかったが稼働状況にはいるシリアル（権限や取得タイミングのずれ）
        foreach ((string serial, JsonElement status) in byStatus)
        {
            if (seen.Contains(serial)) continue;

            rows.Add(BuildDeviceRow(default, status, networkNames, runningVersions));
        }

        return rows;
    }

    private static MerakiDeviceRow BuildDeviceRow(
        JsonElement device, JsonElement status, Dictionary<string, string> networkNames,
        IReadOnlyDictionary<string, string>? runningVersions)
    {
        // 名前と型番はどちらの応答にも入りうる。空でない方を採る
        string networkId = Or(Str(device, "networkId"), Str(status, "networkId"));
        (string text, ConnectionStateKind kind) = DescribeDeviceStatus(Str(status, "status"));

        string serial = Or(Str(device, "serial"), Str(status, "serial"));
        string running = runningVersions is not null
                         && runningVersions.TryGetValue(serial, out string? version) ? version : "";

        return new MerakiDeviceRow(
            Name: Or(Str(device, "name"), Str(status, "name")),
            Model: Or(Str(device, "model"), Str(status, "model")),
            Serial: serial,
            Firmware: DescribeFirmware(Str(device, "firmware"), running),
            Network: networkNames.TryGetValue(networkId, out string? name) ? name : networkId,
            State: text,
            StateKind: kind,
            LanIp: Or(Str(status, "lanIp"), Str(device, "lanIp")));
    }

    // ===== ファーム =====

    /// <summary>
    /// 機器が設定どおりの版で動いていないとき、<c>firmware</c> に版の代わりに入ってくる英文。
    /// <b>版が入るはずの項目に文章が返る</b>ので、そのまま出すと一覧が読めない。
    /// </summary>
    public const string FirmwareMismatch = "Not running configured version";

    /// <summary>
    /// ファームの表示。<b>実際に動いている版は <c>firmware</c> からは分からない</b>ので、
    /// 更新の記録（<see cref="RunningVersions"/>）から補う。
    /// 補えなかったときは版を騙らず、設定と違うことだけを出す。
    /// </summary>
    public static string DescribeFirmware(string? firmware, string running = "")
    {
        string text = firmware ?? "";

        if (!text.Contains(FirmwareMismatch, StringComparison.OrdinalIgnoreCase)) return text;

        return running.Length > 0 ? $"{running}（⚠ 設定と違う）" : "⚠ 設定と違う版";
    }

    /// <summary>
    /// シリアル → いま動いている版。組織のファーム更新の記録から、
    /// <b>完了した更新の「更新後の版」</b>を採る（進行中や取り消しは信用しない）。
    /// 記録が無い機器は入らない — 分からないものを埋めない。
    /// </summary>
    public static IReadOnlyDictionary<string, string> RunningVersions(IEnumerable<string> pages)
    {
        var latest = new Dictionary<string, (string Time, string Version)>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement item in Items(pages))
        {
            string serial = Str(item, "serial");
            if (serial.Length == 0) continue;

            if (!item.TryGetProperty("upgrade", out JsonElement upgrade)
                || upgrade.ValueKind != JsonValueKind.Object)
                continue;

            if (!Str(upgrade, "status").Contains("completed", StringComparison.OrdinalIgnoreCase))
                continue;

            string version = upgrade.TryGetProperty("toVersion", out JsonElement to)
                             && to.ValueKind == JsonValueKind.Object
                ? Or(Str(to, "shortName"), Str(to, "firmware"))
                : "";

            if (version.Length == 0) continue;

            // 同じ機器の記録が何本もある。ISO 8601 なので文字の大小がそのまま時刻の前後
            string time = Str(upgrade, "time");

            if (!latest.TryGetValue(serial, out (string Time, string Version) kept)
                || string.CompareOrdinal(time, kept.Time) > 0)
                latest[serial] = (time, version);
        }

        return latest.ToDictionary(p => p.Key, p => p.Value.Version, StringComparer.OrdinalIgnoreCase);
    }

    // ===== MX の LAN 側アドレス =====

    /// <summary>
    /// MX が持っている LAN 側のアドレス。
    ///
    /// <b>MX の LAN IP は機器の一覧にも稼働状況にも入っていない</b>（あちらの <c>lanIp</c> は
    /// スイッチや AP の管理アドレス）。VLAN を有効にしてある拠点は
    /// <c>appliance/vlans</c>、していない拠点は <c>appliance/singleLan</c> に入っていて、
    /// <b>応答は前者が配列・後者がオブジェクト</b>なので、どちらの形でも読めるようにしてある。
    /// </summary>
    public static IReadOnlyList<string> ApplianceIps(IEnumerable<string> pages)
    {
        var found = new List<string>();

        foreach (JsonElement item in Items(pages).Concat(RootObjects(pages)))
        {
            string ip = Str(item, "applianceIp");

            if (ip.Length > 0 && !found.Contains(ip, StringComparer.OrdinalIgnoreCase))
                found.Add(ip);
        }

        return found;
    }

    /// <summary>
    /// 拠点の LAN 側のセグメント（VLAN のサブネット）。<see cref="ApplianceIps"/> と同じ応答から拾う。
    ///
    /// <b>拠点のセグメントはスタティックルートではなく VLAN で切ってあるのが普通</b>で、
    /// ルートだけを見ると大半の拠点が空欄になる（2026-08-17 に実機で発覚）。
    /// </summary>
    public static IReadOnlyList<string> ApplianceSubnets(IEnumerable<string> pages)
    {
        var found = new List<string>();

        foreach (JsonElement item in Items(pages).Concat(RootObjects(pages)))
        {
            string subnet = Str(item, "subnet");

            if (subnet.Length > 0 && !found.Contains(subnet, StringComparer.OrdinalIgnoreCase))
                found.Add(subnet);
        }

        return found;
    }

    /// <summary>
    /// 機器一覧の MX に LAN 側アドレスを入れる。入れるのは<b>空欄のときだけ</b> —
    /// 機器の応答に入っていたなら、そちらの方が確かなため。
    /// </summary>
    public static IReadOnlyList<MerakiDeviceRow> WithApplianceIps(
        IEnumerable<MerakiDeviceRow> devices, IReadOnlyDictionary<string, string> ipsByNetwork)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(ipsByNetwork);

        var rows = new List<MerakiDeviceRow>();

        foreach (MerakiDeviceRow device in devices)
        {
            bool fill = device.Model.StartsWith("MX", StringComparison.OrdinalIgnoreCase)
                        && device.LanIp.Length == 0
                        && ipsByNetwork.TryGetValue(device.Network, out string? ip)
                        && ip.Length > 0;

            rows.Add(fill ? device with { LanIp = ipsByNetwork[device.Network] } : device);
        }

        return rows;
    }

    // ===== アップリンク =====

    /// <summary>
    /// MX のアップリンク状況。1 台の応答に uplinks 配列（WAN1/WAN2）が入っているので、
    /// 回線 1 本を 1 行に展開する。
    /// </summary>
    public static IReadOnlyList<MerakiUplinkRow> ParseUplinks(
        IEnumerable<string> pages, IReadOnlyList<MerakiNetworkRow> networks)
    {
        Dictionary<string, string> networkNames = networks
            .GroupBy(n => n.Id)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

        var rows = new List<MerakiUplinkRow>();

        foreach (JsonElement item in Items(pages))
        {
            string networkId = Str(item, "networkId");
            string network = networkNames.TryGetValue(networkId, out string? name) ? name : networkId;
            string serial = Str(item, "serial");

            if (!item.TryGetProperty("uplinks", out JsonElement uplinks)
                || uplinks.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement uplink in uplinks.EnumerateArray())
            {
                (string text, ConnectionStateKind kind) = DescribeUplinkStatus(Str(uplink, "status"));

                rows.Add(new MerakiUplinkRow(
                    Network: network,
                    Serial: serial,
                    Interface: Str(uplink, "interface"),
                    State: text,
                    StateKind: kind,
                    Ip: Str(uplink, "ip"),
                    Gateway: Str(uplink, "gateway"),
                    PublicIp: Str(uplink, "publicIp"),
                    RawStatus: Str(uplink, "status")));
            }
        }

        return rows;
    }

    /// <summary>
    /// 一覧に出さない回線を落とす。
    ///
    /// ①<b>つながっていない回線そのもの</b>（未接続。単線の拠点の WAN2 がこれ。
    /// 2026-08-18 ユーザー指示）②<b>止まっているアプライアンスの回線</b>。
    ///
    /// 機器が落ちていれば回線も当然切れているので、一覧に並べても
    /// 「切れている回線」を数えるときの邪魔にしかならない（2026-08-17 ユーザー指示）。
    ///
    /// <b>機器一覧の状態だけでは足りない</b> — 稼働状況が「online」のまま古いことがあるので、
    /// <b>その機器の回線が 1 本もつながっていない</b>ときも止まっているとみなす
    /// （2026-08-18 に「まだ出てくる」と報告された）。
    /// <b>導入時確認では落とさない</b> — あちらは「取れなかった」と「切れている」を区別する。
    /// </summary>
    public static IReadOnlyList<MerakiUplinkRow> WithoutOfflineDevices(
        IEnumerable<MerakiUplinkRow> uplinks, IEnumerable<MerakiDeviceRow> devices)
    {
        ArgumentNullException.ThrowIfNull(uplinks);
        ArgumentNullException.ThrowIfNull(devices);

        MerakiUplinkRow[] rows = [.. uplinks];

        // シリアルの大小は応答によって揺れる（機器一覧と回線の突き合わせも同じ規則）
        var offline = new HashSet<string>(
            devices.Where(d => IsDeviceOffline(d.State) && d.Serial.Length > 0).Select(d => d.Serial),
            StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, MerakiUplinkRow> byDevice in
                 rows.GroupBy(r => r.Serial, StringComparer.OrdinalIgnoreCase))
        {
            if (byDevice.Key.Length > 0 && byDevice.All(r => !IsUplinkUp(r.RawStatus)))
                offline.Add(byDevice.Key);
        }

        // 使っていない WAN2 が「未接続」で並ぶと、切れている回線を数えるときの邪魔になる
        return [.. rows.Where(u => !offline.Contains(u.Serial) && !IsNeverConnected(u.RawStatus))];
    }

    /// <summary>そもそも繋いでいない回線か（<c>not connected</c>）。障害とは区別する。</summary>
    private static bool IsNeverConnected(string? rawStatus)
        => string.Equals(rawStatus, "not connected", StringComparison.OrdinalIgnoreCase);

    /// <summary>機器として止まっているか。休止も止まっている側に入れる。</summary>
    private static bool IsDeviceOffline(string? state)
        => state == OfflineState || state == DormantState;

    /// <summary>
    /// その回線がつながっているか。<c>ready</c> は冗長側の待機なので<b>つながっている</b>側
    /// （導入時確認の判定と同じ規則）。
    /// </summary>
    private static bool IsUplinkUp(string? rawStatus)
        => rawStatus is "active" or "ready" or "connecting";

    /// <summary>アップリンクのグローバル IP を 1 行にまとめる。重複は畳む。</summary>
    public static string GlobalIpSummary(IEnumerable<MerakiUplinkRow> rows)
    {
        string[] addresses = [.. rows
            .Select(r => r.PublicIp)
            .Where(ip => ip.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        return addresses.Length == 0 ? "—" : string.Join(" / ", addresses);
    }

    // ===== クライアント =====

    public static IReadOnlyList<MerakiClientRow> ParseClients(IEnumerable<string> pages, string network = "")
    {
        var rows = new List<MerakiClientRow>();

        foreach (JsonElement item in Items(pages))
        {
            rows.Add(new MerakiClientRow(
                Network: network,
                Description: Or(Str(item, "description"), Str(item, "dhcpHostname")),
                Ip: Str(item, "ip"),
                Mac: Str(item, "mac"),
                Vlan: Scalar(item, "vlan"),
                Manufacturer: Str(item, "manufacturer"),
                Usage: DescribeUsage(item),
                LastSeen: Str(item, "lastSeen")));
        }

        return rows;
    }

    /// <summary>usage は { sent, recv } で単位は KB。合計を人間可読にする。</summary>
    private static string DescribeUsage(JsonElement client)
    {
        if (!client.TryGetProperty("usage", out JsonElement usage)
            || usage.ValueKind != JsonValueKind.Object)
            return "—";

        return FormatKilobytes(Number(usage, "sent") + Number(usage, "recv"));
    }

    /// <summary>
    /// キロバイトを人が読める量にする。<b>クライアントの通信量と回線の通信量で同じ書式</b>を使う
    /// （画面をまたいで単位が変わると比べられない）。
    /// </summary>
    public static string FormatKilobytes(double kilobytes)
    {
        if (kilobytes <= 0) return "0 KB";
        if (kilobytes < 1024) return kilobytes.ToString("F0", CultureInfo.InvariantCulture) + " KB";

        double megabytes = kilobytes / 1024;
        if (megabytes < 1024) return megabytes.ToString("F1", CultureInfo.InvariantCulture) + " MB";

        double gigabytes = megabytes / 1024;

        // 1TB を超えても GB のままだと桁が読めない（2026-08-18 ユーザー指示）
        return gigabytes < 1024
            ? gigabytes.ToString("F1", CultureInfo.InvariantCulture) + " GB"
            : (gigabytes / 1024).ToString("F2", CultureInfo.InvariantCulture) + " TB";
    }

    // ===== 状態の表示 =====

    /// <summary>停止している機器の状態表示。回線を省く判定でも見るので 1 か所に置く。</summary>
    public const string OfflineState = "✕ 停止";

    /// <summary>休止している機器の状態表示。止まっている側として扱う。</summary>
    public const string DormantState = "◌ 休止";

    /// <summary>
    /// 機器の状態。状態は色だけで表さない決まりなので記号を併記する
    /// （記号と種別の対応は接続タブの TcpStateText と揃えてある）。
    /// </summary>
    public static (string Text, ConnectionStateKind Kind) DescribeDeviceStatus(string? status) => status switch
    {
        "online" => ("● 稼働", ConnectionStateKind.Ok),
        "alerting" => ("⊘ 警報", ConnectionStateKind.Info),
        "dormant" => (DormantState, ConnectionStateKind.Muted),
        "offline" => (OfflineState, ConnectionStateKind.Muted),
        null or "" => ("—", ConnectionStateKind.Muted),
        // 知らない値は言い換えずにそのまま出す
        _ => (status, ConnectionStateKind.Muted),
    };

    /// <summary>アップリンクの状態。</summary>
    public static (string Text, ConnectionStateKind Kind) DescribeUplinkStatus(string? status) => status switch
    {
        "active" => ("● 稼働", ConnectionStateKind.Ok),
        "ready" => ("◌ 待機", ConnectionStateKind.Info),
        "connecting" => ("◌ 接続中", ConnectionStateKind.Info),
        "not connected" => ("✕ 未接続", ConnectionStateKind.Muted),
        "failed" => ("✕ 障害", ConnectionStateKind.Muted),
        null or "" => ("—", ConnectionStateKind.Muted),
        _ => (status, ConnectionStateKind.Muted),
    };

    // ===== 拠点（スタティックルート・クライアント数） =====

    /// <summary>スタティックルートの宛先セグメント。無効なものは落とす。</summary>
    public static IReadOnlyList<string> ParseStaticRouteSubnets(IEnumerable<string> pages)
    {
        var subnets = new List<string>();

        foreach (JsonElement item in Items(pages))
        {
            // enabled が無い版もある。明示的に false のときだけ落とす
            if (Scalar(item, "enabled") == "false") continue;

            string subnet = Str(item, "subnet");
            if (subnet.Length > 0) subnets.Add(subnet);
        }

        return subnets;
    }

    /// <summary>
    /// クライアントが 1 台でも居るセグメントだけを残す。
    ///
    /// <b>誰も居ないセグメントは出さない</b>（設定だけ残っている経路を並べても、
    /// 拠点の実態を読み違えるだけ。2026-08-17 ユーザー指示）。
    /// </summary>
    public static IReadOnlyList<string> SegmentsWithClients(
        IEnumerable<string> subnets, IEnumerable<string> clientIps)
    {
        List<IPAddress> addresses =
        [
            .. clientIps.Select(ip => IPAddress.TryParse(ip, out IPAddress? parsed) ? parsed : null)
                .Where(ip => ip is not null && ip.AddressFamily == AddressFamily.InterNetwork)
                .Select(ip => ip!),
        ];

        var kept = new List<string>();

        foreach (string subnet in subnets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryParseCidr(subnet, out IPAddress? network, out int prefix)) continue;

            if (addresses.Any(ip => IpMath.IsSameSubnet(ip, network!, prefix)))
                kept.Add(subnet);
        }

        return kept;
    }

    /// <summary>
    /// 機器一覧の LAN IP に、その拠点のセグメントを足す。
    /// 足すのは MX（拠点の出入口）だけ — スイッチや AP に経路の話を混ぜると読みにくい。
    /// </summary>
    public static IReadOnlyList<MerakiDeviceRow> WithSegments(
        IEnumerable<MerakiDeviceRow> devices, IReadOnlyDictionary<string, string> segmentsByNetwork)
    {
        var rows = new List<MerakiDeviceRow>();

        foreach (MerakiDeviceRow device in devices)
        {
            bool isAppliance = device.Model.StartsWith("MX", StringComparison.OrdinalIgnoreCase);

            if (isAppliance
                && segmentsByNetwork.TryGetValue(device.Network, out string? segments)
                && segments.Length > 0)
            {
                rows.Add(device with { LanIp = Or(device.LanIp, "") + (device.LanIp.Length > 0 ? " / " : "") + segments });
            }
            else
            {
                rows.Add(device);
            }
        }

        return rows;
    }

    public static MerakiSiteRow SiteRow(
        MerakiNetworkRow network, int clients, IReadOnlyList<string> segments, string note) => new(
            Network: network.Name,
            NetworkId: network.Id,
            Clients: clients,
            ClientsText: clients.ToString(CultureInfo.InvariantCulture),
            Segments: string.Join(" / ", segments),
            Note: note);

    private static bool TryParseCidr(string cidr, out IPAddress? network, out int prefix)
    {
        network = null;
        prefix = 0;

        string[] parts = cidr.Split('/');

        if (parts.Length != 2) return false;
        if (!IPAddress.TryParse(parts[0], out network)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix)) return false;

        return prefix is >= 0 and <= 32 && network.AddressFamily == AddressFamily.InterNetwork;
    }

    // ===== DHCP =====

    /// <summary>MX 1 台ぶんの DHCP の払い出し状況。</summary>
    public static IReadOnlyList<MerakiDhcpRow> ParseDhcp(
        IEnumerable<string> pages, string network, string device)
    {
        var rows = new List<MerakiDhcpRow>();

        foreach (JsonElement item in Items(pages))
        {
            int used = (int)Number(item, "usedCount");
            int free = (int)Number(item, "freeCount");
            int total = used + free;
            int percent = total > 0 ? (int)Math.Round(used * 100.0 / total) : -1;

            (string usageText, SeverityKind usageKind) = DescribeDhcpUsage(percent);

            rows.Add(new MerakiDhcpRow(
                Network: network,
                Device: device,
                Vlan: Scalar(item, "vlanId"),
                Subnet: Str(item, "subnet"),
                Used: used,
                UsedText: used.ToString(CultureInfo.InvariantCulture),
                Free: free,
                FreeText: free.ToString(CultureInfo.InvariantCulture),
                UsagePercent: percent,
                UsageText: usageText,
                UsageKind: usageKind));
        }

        return rows;
    }

    /// <summary>
    /// 払い出しの詰まり具合。<b>枯れる前に気づけるところで色を変える</b>
    /// （足りなくなってからでは、その拠点は何も繋がらない）。
    /// </summary>
    public static (string Text, SeverityKind Kind) DescribeDhcpUsage(int percent) => percent switch
    {
        < 0 => ("—", SeverityKind.Muted),
        >= 90 => ($"{percent}%", SeverityKind.Alert),
        >= 70 => ($"{percent}%", SeverityKind.Notice),
        _ => ($"{percent}%", SeverityKind.Ok),
    };

    // ===== 通信量 =====

    /// <summary>
    /// 拠点ごとの WAN の通信量。<b>応答は期間内の合計（単位はキロバイト）</b>なので、
    /// そのまま量として出す（毎秒に直さない。2026-08-17 ユーザー指示）。
    ///
    /// <b>回線ごとには割らず、拠点で 1 行にまとめる</b>（2026-08-18 ユーザー指示）。
    /// 見たいのは「どの拠点がどれだけ使ったか」で、WAN1 と WAN2 の内訳ではない。
    /// </summary>
    public static IReadOnlyList<MerakiTrafficRow> ParseTraffic(IEnumerable<string> pages)
    {
        var byNetwork = new Dictionary<string, (double Sent, double Received)>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (JsonElement item in Items(pages))
        {
            string network = Or(Str(item, "name"), Str(item, "networkId"));

            if (!item.TryGetProperty("byUplink", out JsonElement uplinks)
                || uplinks.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement uplink in uplinks.EnumerateArray())
            {
                if (uplink.ValueKind != JsonValueKind.Object) continue;

                if (!byNetwork.TryGetValue(network, out (double Sent, double Received) sum))
                    order.Add(network);

                byNetwork[network] = (
                    sum.Sent + Number(uplink, "sent"),
                    sum.Received + Number(uplink, "received"));
            }
        }

        var rows = new List<MerakiTrafficRow>();

        foreach (string network in order)
        {
            (double sent, double received) = byNetwork[network];

            rows.Add(new MerakiTrafficRow(
                Network: network,
                Kilobytes: sent + received,
                Total: FormatKilobytes(sent + received),
                Sent: FormatKilobytes(sent),
                Received: FormatKilobytes(received)));
        }

        return rows;
    }

    // ===== 利用率 =====

    // ===== アラート =====

    public static IReadOnlyList<MerakiAlertRow> ParseAlerts(IEnumerable<string> pages)
    {
        var rows = new List<MerakiAlertRow>();

        foreach (JsonElement item in Items(pages))
        {
            (string severity, SeverityKind kind) = DescribeAlertSeverity(Str(item, "severity"));

            rows.Add(new MerakiAlertRow(
                Severity: severity,
                SeverityKind: kind,
                Type: Or(Str(item, "categoryType"), Or(Str(item, "type"), Str(item, "title"))),
                Network: Nested(item, "network", "name"),
                Device: Or(Nested(item, "device", "name"), Nested(item, "scope", "devices")),
                StartedAt: Or(Str(item, "startedAt"), Str(item, "occurredAt")),
                Detail: Or(Str(item, "title"), Str(item, "description"))));
        }

        return rows;
    }

    /// <summary>アラートの重さ。<b>知らない値はそのまま出す</b>。</summary>
    public static (string Text, SeverityKind Kind) DescribeAlertSeverity(string? severity) => severity switch
    {
        "critical" => ("✕ 重大", SeverityKind.Alert),
        "warning" => ("⊘ 警告", SeverityKind.Notice),
        "informational" or "info" => ("● 情報", SeverityKind.Ok),
        null or "" => ("—", SeverityKind.Muted),
        _ => (severity, SeverityKind.Muted),
    };

    /// <summary>入れ子の項目を 1 段だけ辿る。無ければ空文字。</summary>
    private static string Nested(JsonElement parent, string name, string child)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement inner)
           && inner.ValueKind == JsonValueKind.Object
            ? Str(inner, child)
            : "";

    // ===== CSV =====

    public static CsvTable ToCsv(IReadOnlyList<MerakiNetworkRow> rows) => new(
        ["ネットワーク", "ID", "製品", "タイムゾーン", "タグ"],
        [.. rows.Select(r => new[] { r.Name, r.Id, r.ProductTypes, r.TimeZone, r.Tags })]);

    public static CsvTable ToCsv(IReadOnlyList<MerakiDeviceRow> rows) => new(
        ["名前", "型番", "シリアル", "ファーム", "ネットワーク", "状態", "LAN IP"],
        [.. rows.Select(r => new[] { r.Name, r.Model, r.Serial, r.Firmware, r.Network, r.State, r.LanIp })]);

    public static CsvTable ToCsv(IReadOnlyList<MerakiUplinkRow> rows) => new(
        ["ネットワーク", "シリアル", "回線", "状態", "IP", "ゲートウェイ", "グローバル IP"],
        [.. rows.Select(r => new[] { r.Network, r.Serial, r.Interface, r.State, r.Ip, r.Gateway, r.PublicIp })]);

    public static CsvTable ToCsv(IReadOnlyList<MerakiSiteRow> rows) => new(
        ["拠点", "ID", "クライアント数", "セグメント", "備考"],
        [.. rows.Select(r => new[] { r.Network, r.NetworkId, r.ClientsText, r.Segments, r.Note })]);

    public static CsvTable ToCsv(IReadOnlyList<MerakiDhcpRow> rows) => new(
        ["拠点", "機器", "VLAN", "サブネット", "払い出し済み", "空き", "使用率"],
        [.. rows.Select(r => new[] { r.Network, r.Device, r.Vlan, r.Subnet, r.UsedText, r.FreeText, r.UsageText })]);

    public static CsvTable ToCsv(IReadOnlyList<MerakiTrafficRow> rows) => new(
        ["拠点", "合計", "送信", "受信"],
        [.. rows.Select(r => new[] { r.Network, r.Total, r.Sent, r.Received })]);

    public static CsvTable ToCsv(IReadOnlyList<MerakiAlertRow> rows) => new(
        ["重大度", "種別", "拠点", "機器", "発生", "内容"],
        [.. rows.Select(r => new[] { r.Severity, r.Type, r.Network, r.Device, r.StartedAt, r.Detail })]);

    public static CsvTable ToCsv(IReadOnlyList<MerakiClientRow> rows) => new(
        ["拠点", "名前", "IP", "MAC", "VLAN", "メーカー", "通信量", "最終確認"],
        [.. rows.Select(r => new[]
        {
            r.Network, r.Description, r.Ip, r.Mac, r.Vlan, r.Manufacturer, r.Usage, r.LastSeen,
        })]);

    // ===== HTTP の応答から読み取る小物（HTTP そのものには触らない） =====

    /// <summary>
    /// Link ヘッダから次ページの URL を取る。
    /// 形は <c>&lt;https://...&gt;; rel=first, &lt;https://...&gt;; rel=next</c>
    /// （rel の値は引用符が付く場合と付かない場合がある）。
    /// </summary>
    public static string? NextPageUrl(string? linkHeader)
    {
        if (string.IsNullOrWhiteSpace(linkHeader)) return null;

        foreach (string part in linkHeader.Split(','))
        {
            int open = part.IndexOf('<');
            int close = part.IndexOf('>');
            if (open < 0 || close <= open) continue;

            string rel = part[(close + 1)..].Replace("\"", "").Replace(" ", "");
            if (!rel.Contains("rel=next", StringComparison.OrdinalIgnoreCase)) continue;

            string url = part[(open + 1)..close].Trim();
            if (url.Length > 0) return url;
        }

        return null;
    }

    /// <summary>Retry-After（秒）を 1〜60 秒に丸める。日付形式や空は既定値。</summary>
    public static int RetryAfterSeconds(string? header, int fallback = 1)
    {
        if (!int.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            return fallback;

        return Math.Clamp(seconds, 1, 60);
    }

    /// <summary>応答コードを日本語にする。本文は読まない（キーが載る可能性を持ち込まない）。</summary>
    public static string DescribeFailure(int statusCode) => statusCode switch
    {
        400 => "要求の内容が正しくありません（400）。",
        401 => "API キーが正しくありません（401）。ダッシュボードの [My profile] で発行したキーを確認してください。",
        403 => "このキーでは参照できません（403）。組織へのアクセス権を確認してください。",
        404 => "見つかりませんでした（404）。組織やネットワークが変わっていないか確認してください。",
        429 => "呼び出しが多すぎます（429）。しばらく待ってからもう一度取得してください。",
        >= 500 and < 600 => $"Meraki 側で処理できませんでした（{statusCode}）。時間をおいて試してください。",
        _ => $"取得できませんでした（HTTP {statusCode}）。",
    };

    // ===== JSON の小物（導入時確認からも使うので internal） =====

    /// <summary>各ページ（JSON 配列）の要素を順に返す。配列でないページは読み飛ばす。</summary>
    internal static IEnumerable<JsonElement> Items(IEnumerable<string> pages)
    {
        foreach (string page in pages)
        {
            if (string.IsNullOrWhiteSpace(page)) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(page);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array) continue;

                // using を抜けると無効になるので複製して返す。
                // オブジェクト以外を混ぜない（TryGetProperty は Object 以外で例外を投げる）
                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                        yield return item.Clone();
                }
            }
        }
    }

    /// <summary>応答そのものがオブジェクトのとき（<c>singleLan</c> など）の受け口。</summary>
    private static IEnumerable<JsonElement> RootObjects(IEnumerable<string> pages)
    {
        foreach (string page in pages)
        {
            if (string.IsNullOrWhiteSpace(page)) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(page);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    yield return document.RootElement.Clone();
            }
        }
    }

    internal static string Str(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>数値でも文字列でも受ける項目（clients の vlan など）。</summary>
    internal static string Scalar(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out JsonElement value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    internal static double Number(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out double number)
            ? number
            : 0;

    private static string Join(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
            return "";

        return string.Join(", ", value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()));
    }

    private static string Or(string first, string second) => first.Length > 0 ? first : second;
}
