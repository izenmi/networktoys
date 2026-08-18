using System.Globalization;
using System.Text.Json;
using NetworkToys.Core.Net;
using NetworkToys.Core.Verify;
using NetworkToys.Core.Work;

namespace NetworkToys.Core.Cloud;

/// <summary>
/// 導入時確認の 1 行。<b>1 行 = 1 つの確認項目</b>（対象が複数ある項目は対象ごとに 1 行）。
///
/// 合否は試験タブと同じ <see cref="CheckVerdict"/> を使う。判定できないものを
/// 合格に丸めないこと — 現場で拾えなくなる。
/// </summary>
/// <param name="Name">項目名。</param>
/// <param name="Target">見た相手（機器・回線・セグメント）。</param>
/// <param name="Verdict">合否。</param>
/// <param name="Detail">根拠。<b>不合格のときは次にどこを見ればよいかまで書く。</b></param>
public sealed record MerakiCheckRow(
    string Name,
    string Target,
    CheckVerdict Verdict,
    string Detail)
{
    /// <summary>
    /// 画面に出す合否。文字は試験タブと共通だが、<b>ここでは何も「試験」しない</b>ので
    /// 取れなかったものだけ言い方を変える。
    /// </summary>
    public string VerdictText => Verdict == CheckVerdict.Skipped
        ? "— 確認できず"
        : CheckVerdictText.Of(Verdict);

    public bool IsPass => Verdict == CheckVerdict.Pass;
    public bool IsFail => Verdict == CheckVerdict.Fail;
    public bool IsWarn => Verdict == CheckVerdict.Warn;

    /// <summary>人が見て合否を付ける番（API から判定できない項目）。</summary>
    public bool NeedsPerson => Verdict == CheckVerdict.AwaitingPerson;
}

/// <summary>
/// MX を入れた日に、その拠点が正常に導入されたかを 1 画面で確かめる。
///
/// ここは HTTP に触らない（<c>MerakiCatalog</c> と同じ）。応答の JSON 文字列と、
/// すでに一覧になっている行を受け取って合否だけを決めるので、固定のサンプルで検証できる。
///
/// 判定の考え方:
/// ・<b>取れなかったものを合格にしない。</b>「— 確認できず」と理由を出す
/// ・<b>1 項目の失敗で全体を止めない。</b>呼ぶ側が項目ごとに捕まえて <see cref="Unavailable"/> を積む
/// ・<b>数えられないものは人に渡す。</b>MX のポート速度は API に無いので目視の行にする
/// </summary>
public static class MerakiInstallCheck
{
    // ===== 項目名（画面と CSV に出る。1 か所にまとめる） =====

    public const string DevicesName = "機器の稼働";
    public const string WanName = "インターネット回線";
    public const string QualityName = "回線の品質";
    public const string PortsName = "ポートの速度・全二重";
    public const string VpnName = "VPN（IPsec）";
    public const string DhcpName = "DHCP";
    public const string ClientsName = "クライアント";

    // ===== 目安（実機で緩めたくなったらここだけ動かす） =====

    /// <summary>これを超えるロスは不合格。通話も画面共有も成立しない。</summary>
    public const double LossFailPercent = 5;

    /// <summary>ここから注意。<b>0% でないこと自体は珍しくない</b>ので不合格にはしない。</summary>
    public const double LossWarnPercent = 1;

    /// <summary>国内の拠点でこれを超えるなら回線か経路を疑う。</summary>
    public const double LatencyWarnMs = 150;

    /// <summary>求める速度。<b>これ以上なら合格</b>（10G の上位ポートを不合格にしない）。</summary>
    public const double RequiredSpeedMbps = 1000;

    // ===== 1. 機器の稼働 =====

    /// <summary>その拠点の機器がすべて online か。API を足さずに機器一覧から判る。</summary>
    public static MerakiCheckRow Devices(IReadOnlyList<MerakiDeviceRow> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        if (devices.Count == 0)
            return new(DevicesName, "—", CheckVerdict.Skipped, "この拠点の機器が一覧にありません。先に「取得」を押してください。");

        string[] bad =
        [
            .. devices.Where(d => d.StateKind != ConnectionStateKind.Ok)
                .Select(d => $"{NameOf(d)}（{d.State}）"),
        ];

        int alive = devices.Count - bad.Length;

        // <b>1 台でも動いていれば合格</b>（2026-08-18 ユーザー指示）。
        // 予備機や撤去待ちが停止のまま残る拠点があり、全台稼働を求めると必ず落ちる。
        // 止まっている機器は<b>合格のまま詳細に並べる</b> — 黙って消さない
        if (alive == 0)
        {
            return new(DevicesName, $"{devices.Count} 台", CheckVerdict.Fail,
                       $"稼働している機器がありません: {Listed(bad)}");
        }

        return bad.Length == 0
            ? new(DevicesName, $"{devices.Count} 台", CheckVerdict.Pass, $"{devices.Count} 台すべて稼働しています。")
            : new(DevicesName, $"{devices.Count} 台", CheckVerdict.Pass,
                  $"{alive} 台が稼働しています。"
                  + $"止まっている機器が {bad.Length} 台あります: {Listed(bad)}");
    }

    // ===== 2. インターネット回線（WAN） =====

    /// <summary>
    /// <b>有効にしてある WAN が全部リンクアップしているか。</b>
    ///
    /// どの WAN を使う設定にしてあるかは <c>appliance/uplinks/settings</c> にしか無い。
    /// 状態（<c>appliance/uplink/statuses</c>）だけで見ると、
    /// <b>そもそも使っていない WAN2 が「未接続」で不合格になる</b>。
    ///
    /// 設定が取れなかったときは状態だけで見て「注意」にする（黙って合格にしない）。
    /// </summary>
    public static MerakiCheckRow Wan(
        string device, IEnumerable<string> settingsPages, IReadOnlyList<MerakiUplinkRow> uplinks)
    {
        ArgumentNullException.ThrowIfNull(uplinks);

        IReadOnlyList<string>? enabled = ParseEnabledUplinks(settingsPages);

        if (uplinks.Count == 0)
        {
            return new(WanName, device, CheckVerdict.Skipped,
                       "この機器の回線の状態が取れていません。先に「取得」を押してください。");
        }

        string state = string.Join(" / ", uplinks.Select(u => $"{u.Interface} {u.State}"));

        // 設定が取れなかった: リンクアップしている回線が 1 本でもあるかだけを見る
        if (enabled is null)
        {
            bool anyUp = uplinks.Any(IsLinkedUp);

            return new(WanName, device, anyUp ? CheckVerdict.Warn : CheckVerdict.Fail,
                       $"どの WAN を使う設定かが取れなかったので、状態だけで見ています（{state}）。"
                       + (anyUp ? "" : " リンクアップしている回線がありません。"));
        }

        if (enabled.Count == 0)
        {
            return new(WanName, device, CheckVerdict.Fail,
                       $"有効になっている WAN がありません（{state}）。ダッシュボードの Uplink configuration を確認してください。");
        }

        var down = new List<string>();

        foreach (string name in enabled)
        {
            MerakiUplinkRow? row = uplinks.FirstOrDefault(
                u => string.Equals(u.Interface, name, StringComparison.OrdinalIgnoreCase));

            if (row is null)
                down.Add($"{name}（状態が取れません）");
            else if (!IsLinkedUp(row))
                down.Add($"{name}（{row.State}）");
        }

        string enabledText = string.Join(" / ", enabled);

        return down.Count == 0
            ? new(WanName, device, CheckVerdict.Pass, $"有効な回線 {enabledText} はすべてリンクアップしています（{state}）。")
            : new(WanName, device, CheckVerdict.Fail,
                  $"リンクアップしていない回線があります: {Listed(down)}（有効: {enabledText}）。ケーブルと ONU の口を確認してください。");
    }

    /// <summary>
    /// リンクアップとみなす状態。<c>ready</c> は待機（冗長側で正常）だが、
    /// <b><c>connecting</c> はまだリンクアップしていない</b>ので含めない。
    /// </summary>
    public static bool IsLinkedUp(MerakiUplinkRow uplink)
    {
        ArgumentNullException.ThrowIfNull(uplink);

        // 古い応答で status が空のときは、表示用の種別で代替する（active だけが Ok）
        return uplink.RawStatus.Length > 0
            ? uplink.RawStatus is "active" or "ready"
            : uplink.StateKind == ConnectionStateKind.Ok;
    }

    /// <summary>
    /// 有効な WAN の名前（wan1 / wan2）。応答は配列ではなく
    /// <c>{"interfaces":{"wan1":{"enabled":true,…}}}</c> の 1 個。
    /// <b>読めなければ null</b>（「有効な WAN が 0 本」とは意味が違う）。
    /// </summary>
    public static IReadOnlyList<string>? ParseEnabledUplinks(IEnumerable<string> pages)
    {
        List<string>? enabled = null;

        foreach (JsonElement root in Objects(pages))
        {
            if (!root.TryGetProperty("interfaces", out JsonElement interfaces)
                || interfaces.ValueKind != JsonValueKind.Object)
                continue;

            enabled ??= [];

            foreach (JsonProperty each in interfaces.EnumerateObject())
            {
                if (each.Value.ValueKind != JsonValueKind.Object) continue;

                // enabled を持たない版は「使う」とみなす（黙って落とすと確認が漏れる）
                if (MerakiCatalog.Scalar(each.Value, "enabled") != "false")
                    enabled.Add(each.Name);
            }
        }

        return enabled;
    }

    // ===== 3. 回線の品質（ロス・遅延） =====

    /// <summary>
    /// 回線ごとのロスと遅延。応答は回線 1 本ぶんが 1 要素で、
    /// <c>timeSeries</c> に区間ごとの値が入る（<b>値は null になりうる</b>）。
    /// </summary>
    public static IReadOnlyList<MerakiCheckRow> Quality(
        IEnumerable<string> pages, IReadOnlyList<MerakiDeviceRow> appliances)
    {
        ArgumentNullException.ThrowIfNull(appliances);

        Dictionary<string, string> names = appliances
            .Where(d => d.Serial.Length > 0)
            .GroupBy(d => d.Serial, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => NameOf(g.First()), StringComparer.OrdinalIgnoreCase);

        var rows = new List<MerakiCheckRow>();

        foreach (JsonElement item in MerakiCatalog.Items(pages))
        {
            string serial = MerakiCatalog.Str(item, "serial");
            if (!names.TryGetValue(serial, out string? device)) continue;

            string uplink = MerakiCatalog.Str(item, "uplink");
            string probe = MerakiCatalog.Str(item, "ip");
            string target = $"{device} {uplink}".Trim();

            var loss = new List<double>();
            var latency = new List<double>();

            if (item.TryGetProperty("timeSeries", out JsonElement series)
                && series.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement point in series.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Object) continue;

                    if (point.TryGetProperty("lossPercent", out JsonElement l)
                        && l.ValueKind == JsonValueKind.Number && l.TryGetDouble(out double lossValue))
                        loss.Add(lossValue);

                    if (point.TryGetProperty("latencyMs", out JsonElement m)
                        && m.ValueKind == JsonValueKind.Number && m.TryGetDouble(out double latencyValue))
                        latency.Add(latencyValue);
                }
            }

            if (loss.Count == 0 && latency.Count == 0)
            {
                rows.Add(new(QualityName, target, CheckVerdict.Skipped,
                             "実測値がまだありません（リンクアップした直後は数分かかります）。"));
                continue;
            }

            double lossAverage = loss.Count > 0 ? loss.Average() : 0;
            double lossMax = loss.Count > 0 ? loss.Max() : 0;
            double latencyAverage = latency.Count > 0 ? latency.Average() : 0;

            CheckVerdict verdict = lossAverage > LossFailPercent || lossMax > LossFailPercent * 2
                ? CheckVerdict.Fail
                : lossAverage > LossWarnPercent || latencyAverage > LatencyWarnMs
                    ? CheckVerdict.Warn
                    : CheckVerdict.Pass;

            string detail =
                $"ロス 平均 {Fixed(lossAverage, 1)}% ／ 最大 {Fixed(lossMax, 1)}%、"
                + $"遅延 平均 {Fixed(latencyAverage, 0)} ms"
                + (probe.Length > 0 ? $"（{probe} 宛）" : "");

            rows.Add(new(QualityName, target, verdict, detail));
        }

        if (rows.Count == 0)
        {
            rows.Add(new(QualityName, "—", CheckVerdict.Skipped,
                         "この拠点の回線の実測値がありません（測り始めるまで数分かかります）。"));
        }

        return rows;
    }

    // ===== 4. ポートの速度・全二重 =====

    /// <summary>
    /// スイッチ 1 台のポート。<b>つながっているポートだけ</b>を見る
    /// （空きポートが遅くても困らない）。
    /// </summary>
    public static MerakiCheckRow SwitchPorts(string device, IEnumerable<string> pages)
    {
        var slow = new List<string>();
        var noisy = new List<string>();
        int connected = 0;

        foreach (JsonElement item in MerakiCatalog.Items(pages))
        {
            string status = MerakiCatalog.Str(item, "status");

            // つながっていないポートは対象外。表記ゆれに備えて含み判定にする
            if (!status.Contains("connect", StringComparison.OrdinalIgnoreCase)
                || status.Contains("disconnect", StringComparison.OrdinalIgnoreCase))
                continue;

            connected++;

            string port = MerakiCatalog.Scalar(item, "portId");
            string speedText = MerakiCatalog.Str(item, "speed");
            string duplex = MerakiCatalog.Str(item, "duplex");
            double? speed = ParseSpeedMbps(speedText);

            bool fast = speed is { } value && value >= RequiredSpeedMbps;
            bool full = duplex.Contains("full", StringComparison.OrdinalIgnoreCase);

            if (!fast || !full)
            {
                slow.Add($"ポート {port}（{Or(speedText, "速度不明")} {Or(duplex, "二重不明")}）");
            }

            string errors = Join(item, "errors");
            string warnings = Join(item, "warnings");

            if (errors.Length > 0 || warnings.Length > 0)
                noisy.Add($"ポート {port}（{Or(errors, warnings)}）");
        }

        if (connected == 0)
        {
            return new(PortsName, device, CheckVerdict.Fail,
                       "つながっているポートが 1 つもありません。ケーブルを確認してください。");
        }

        if (slow.Count > 0)
        {
            return new(PortsName, device, CheckVerdict.Fail,
                       $"1000Mbps/Full でないポートが {slow.Count} 個あります: {Listed(slow)}"
                       + $"（接続 {connected} ポート）");
        }

        return noisy.Count > 0
            ? new(PortsName, device, CheckVerdict.Warn,
                  $"接続 {connected} ポートは 1000Mbps/Full ですが、エラーの出ているポートがあります: {Listed(noisy)}")
            : new(PortsName, device, CheckVerdict.Pass,
                  $"つながっている {connected} ポートはすべて 1000Mbps/Full です。");
    }

    /// <summary>
    /// MX のポート速度・全二重は<b>ダッシュボード API が持っていない</b>（画面にしか無い）。
    /// 行を消すと「確認しなくてよい項目」に見えるので、<b>人に渡す行として必ず出す</b>。
    /// </summary>
    public static MerakiCheckRow AppliancePortsByPerson(string device, string model)
        => new(PortsName, $"{device}（{model}）", CheckVerdict.AwaitingPerson,
               "アプライアンスのポート速度は API から取れません。"
               + "ダッシュボードの Appliance status → Ports で 1000Mbps/Full を確かめて ○ か ✕ を押してください。");

    /// <summary>「1 Gbps」「1000 Mbps」などを Mbps の数にする。読めなければ null。</summary>
    public static double? ParseSpeedMbps(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string trimmed = text.Trim();
        int at = 0;

        while (at < trimmed.Length && (char.IsDigit(trimmed[at]) || trimmed[at] is '.')) at++;

        if (at == 0) return null;

        if (!double.TryParse(trimmed[..at], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return null;

        string unit = trimmed[at..].Trim();

        if (unit.StartsWith("g", StringComparison.OrdinalIgnoreCase)) return value * 1000;
        if (unit.StartsWith("k", StringComparison.OrdinalIgnoreCase)) return value / 1000;

        // 単位が無い版は Mbps とみなす（Meraki は「1 Gbps」形式だが、数だけ返す版に備える）
        return value;
    }

    // ===== 5. VPN =====

    /// <summary>
    /// その拠点から見たトンネルの状態。AutoVPN（Meraki 同士）と
    /// サードパーティ IPsec を<b>分けて 1 行ずつ</b>にする（見るところが違う）。
    /// </summary>
    public static IReadOnlyList<MerakiCheckRow> Vpn(IEnumerable<string> pages, string networkId)
    {
        foreach (JsonElement item in MerakiCatalog.Items(pages))
        {
            if (!string.Equals(MerakiCatalog.Str(item, "networkId"), networkId, StringComparison.OrdinalIgnoreCase))
                continue;

            MerakiCheckRow meraki = PeerRow(item, "merakiVpnPeers", "networkName", "AutoVPN");
            MerakiCheckRow third = PeerRow(item, "thirdPartyVpnPeers", "name", "サードパーティ IPsec");

            return [meraki, third];
        }

        return
        [
            new(VpnName, "—", CheckVerdict.Skipped,
                "この拠点の VPN の状態が応答に入っていません（VPN を使わない拠点ではこうなります）。"),
        ];
    }

    private static MerakiCheckRow PeerRow(JsonElement site, string property, string nameKey, string label)
    {
        if (!site.TryGetProperty(property, out JsonElement peers) || peers.ValueKind != JsonValueKind.Array)
            return new(VpnName, label, CheckVerdict.Skipped, "相手が 1 つも設定されていません。");

        var unreachable = new List<string>();
        int total = 0;

        foreach (JsonElement peer in peers.EnumerateArray())
        {
            if (peer.ValueKind != JsonValueKind.Object) continue;

            total++;

            string reachability = MerakiCatalog.Str(peer, "reachability");

            if (!string.Equals(reachability, "reachable", StringComparison.OrdinalIgnoreCase))
            {
                string name = Or(MerakiCatalog.Str(peer, nameKey), MerakiCatalog.Str(peer, "publicIp"));
                unreachable.Add($"{Or(name, "名前不明")}（{Or(reachability, "状態不明")}）");
            }
        }

        if (total == 0)
            return new(VpnName, label, CheckVerdict.Skipped, "相手が 1 つも設定されていません。");

        return unreachable.Count == 0
            ? new(VpnName, label, CheckVerdict.Pass, $"{total} 拠点すべてに届いています。")
            : new(VpnName, label, CheckVerdict.Fail,
                  $"{total} のうち {unreachable.Count} に届いていません: {Listed(unreachable)}");
    }

    // ===== 6. DHCP =====

    /// <summary>
    /// セグメントごとに、実際に配れているか。
    /// <b>0 件は不合格にしない</b> — 導入直後でまだ端末を繋いでいないだけのことがある。
    /// </summary>
    public static IReadOnlyList<MerakiCheckRow> Dhcp(IReadOnlyList<MerakiDhcpRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return
            [
                new(DhcpName, "—", CheckVerdict.Skipped,
                    "この拠点の MX に DHCP のセグメントがありません（配るのを別の機器に任せている構成ではこうなります）。"),
            ];
        }

        var result = new List<MerakiCheckRow>();

        foreach (MerakiDhcpRow row in rows)
        {
            string target = $"{row.Device} VLAN {row.Vlan} {row.Subnet}".Trim();
            string counts = $"払い出し {row.Used} ／ 空き {row.Free}（使用率 {row.UsageText}）";

            result.Add(row.Used switch
            {
                0 => new MerakiCheckRow(DhcpName, target, CheckVerdict.Warn,
                         $"まだ 1 台も配っていません。{counts} 端末を繋いでから確かめてください。"),
                _ when row.UsagePercent >= 90 => new MerakiCheckRow(DhcpName, target, CheckVerdict.Warn,
                         $"空きが少なくなっています。{counts}"),
                _ => new MerakiCheckRow(DhcpName, target, CheckVerdict.Pass, counts),
            });
        }

        return result;
    }

    // ===== 7. クライアント =====

    /// <summary>その拠点に端末が居るか。0 台なら「まだ何も繋がっていない」。</summary>
    public static MerakiCheckRow Clients(IReadOnlyList<MerakiClientRow> clients, string timespanName)
    {
        ArgumentNullException.ThrowIfNull(clients);

        if (clients.Count == 0)
        {
            return new(ClientsName, $"直近 {timespanName}", CheckVerdict.Fail,
                       "1 台も見えていません。端末を繋いでから確かめてください。");
        }

        int addressed = clients.Count(c => c.Ip.Length > 0);

        return new(ClientsName, $"直近 {timespanName}", CheckVerdict.Pass,
                   $"{clients.Count} 台（うち IP が付いているもの {addressed} 台）。");
    }

    // ===== 取れなかった項目 =====

    /// <summary>
    /// 1 項目が取れなくても<b>ほかの項目は最後まで走らせる</b>ための行。
    /// 合格に丸めず、理由をそのまま残す。
    /// </summary>
    public static MerakiCheckRow Unavailable(string name, string target, string reason)
        => new(name, target, CheckVerdict.Skipped, $"確認できませんでした: {reason}");

    // ===== まとめと CSV =====

    /// <summary>画面の状態欄に出す 1 行。<b>目視待ちが残るうちは「終わった」と言わない</b>。</summary>
    public static string Summarize(IReadOnlyList<MerakiCheckRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0) return "";

        int pass = rows.Count(r => r.IsPass);
        int fail = rows.Count(r => r.IsFail);
        int warn = rows.Count(r => r.IsWarn);
        int skipped = rows.Count(r => r.Verdict == CheckVerdict.Skipped);
        int person = rows.Count(r => r.NeedsPerson);

        var tail = new List<string>();
        if (warn > 0) tail.Add($"注意 {warn}");
        if (skipped > 0) tail.Add($"確認できず {skipped}");

        string extra = tail.Count > 0 ? " / " + string.Join(" / ", tail) : "";
        string pending = person > 0 ? $"　目視の {person} 件に ○ か ✕ を付けてください。" : "";

        if (fail > 0)
            return $"✕ 不合格が {fail} 件あります（合格 {pass}{extra}）。{pending}";

        return warn > 0
            ? $"△ 不合格はありませんが注意が {warn} 件あります（合格 {pass}{extra}）。{pending}"
            : $"自動の判定はすべて合格しました（{pass} 件{extra}）。{pending}";
    }

    public static CsvTable ToCsv(IReadOnlyList<MerakiCheckRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return new CsvTable(
            ["項目", "対象", "判定", "詳細"],
            [.. rows.Select(r => new[] { r.Name, r.Target, r.VerdictText, r.Detail })]);
    }

    // ===== 小物 =====

    /// <summary>名前が無い機器はシリアルで呼ぶ（空欄だと行が誰のものか分からない）。</summary>
    private static string NameOf(MerakiDeviceRow device)
        => device.Name.Length > 0 ? device.Name : device.Serial;

    /// <summary>並べても読めるのは 5 つまで。残りは件数だけ添える。</summary>
    private static string Listed(IReadOnlyList<string> items, int max = 5)
        => items.Count <= max
            ? string.Join("・", items)
            : string.Join("・", items.Take(max)) + $" ほか {items.Count - max} 件";

    private static string Fixed(double value, int digits)
        => value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Or(string first, string second) => first.Length > 0 ? first : second;

    private static string Join(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
            return "";

        return string.Join("・", value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()));
    }

    /// <summary>ページ（JSON オブジェクト）を順に返す。配列のページは読み飛ばす。</summary>
    private static IEnumerable<JsonElement> Objects(IEnumerable<string> pages)
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
}
