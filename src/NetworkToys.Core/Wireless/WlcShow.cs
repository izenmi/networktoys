using System.Globalization;
using NetworkToys.Core.Design;

namespace NetworkToys.Core.Wireless;

/// <summary>
/// Catalyst 9800 の <c>show</c> 出力を、RESTCONF と同じ行に変換する。
///
/// <b>RESTCONF を有効にできない現場がある</b>ので、SSH で流した出力からも
/// 同じ表を作れるようにしてある（2026-08-18 ユーザー指示。それまでは
/// 「解釈せずそのまま見せる」だけだった）。
///
/// 桁の揃え方は版で動く。だから<b>桁位置は決め打ちにせず、見出し行から学習する</b>:
/// ①見出し行の語の開始位置で切る ②切れないときは 2 文字以上の空白で割る。
/// <b>読めない行は捨てる。例外にしない</b> — 1 行のために表全体を失う方が困る
/// （FTP の一覧の解釈と同じ判断）。
/// </summary>
public static class WlcShow
{
    // ===== 見出しの候補（実機で外れたら 1 行足せば直る） =====

    private static readonly string[] ApNameHeads = ["ap name", "ap name/hostname", "name"];
    private static readonly string[] MacHeads = ["mac address", "ethernet mac", "ap mac", "base radio mac", "radio mac"];
    private static readonly string[] IpHeads = ["ip address", "ip"];
    private static readonly string[] ModelHeads = ["ap model", "model"];
    private static readonly string[] StateHeads = ["state", "status", "admin state", "oper state"];
    private static readonly string[] ChannelHeads = ["channel", "chan"];
    private static readonly string[] PowerHeads = ["txpwr", "tx power", "power level", "power"];
    private static readonly string[] SsidHeads = ["ssid", "network name (ssid)", "network name"];
    private static readonly string[] ProfileHeads = ["profile", "profile name", "wlan profile"];
    private static readonly string[] IdHeads = ["id", "wlan id", "wlan"];
    private static readonly string[] ProtocolHeads = ["protocol", "type"];
    private static readonly string[] SlotHeads = ["slot", "slots", "slot id"];

    /// <summary>1 行ぶんの値。見出し名（小文字）で引く。</summary>
    public sealed class ShowRow
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        internal void Set(string head, string value) => _values[head] = value;

        /// <summary>候補の見出しを順に見て、最初に埋まっているものを返す。無ければ空。</summary>
        public string this[params string[] heads]
        {
            get
            {
                foreach (string head in heads)
                {
                    if (_values.TryGetValue(head, out string? value) && value.Length > 0) return value;
                }

                // 見出しが完全一致しないときは前方一致で拾う（"ip address (ipv4)" のような揺れ）
                foreach (string head in heads)
                {
                    foreach ((string key, string value) in _values)
                    {
                        if (value.Length > 0 && key.StartsWith(head, StringComparison.OrdinalIgnoreCase))
                            return value;
                    }
                }

                return "";
            }
        }

        /// <summary>見出しが分からないときのための素の並び。</summary>
        public IReadOnlyList<string> Cells { get; internal set; } = [];
    }

    /// <summary>
    /// 見出し付きの表を読む。
    ///
    /// 見出し行は「区切り線（<c>----</c>）のすぐ上の行」とみなす。区切り線が無い版のために、
    /// 候補の語をいちばん多く含む行も見る。
    /// </summary>
    public static IReadOnlyList<ShowRow> Table(string? text)
    {
        string[] lines = Split(text);

        int headerAt = FindHeader(lines);
        if (headerAt < 0) return [];

        string header = lines[headerAt];
        (string Name, int Start)[] columns = Columns(header);

        if (columns.Length < 2) return [];

        var rows = new List<ShowRow>();

        for (int i = headerAt + 1; i < lines.Length; i++)
        {
            string line = lines[i];

            if (line.Trim().Length == 0) continue;
            if (IsSeparator(line)) continue;
            if (IsNoise(line)) continue;

            string[] cells = Cells(line, columns);

            if (cells.Length == 0) continue;

            var row = new ShowRow { Cells = cells };

            for (int c = 0; c < columns.Length && c < cells.Length; c++)
                row.Set(columns[c].Name, cells[c]);

            rows.Add(row);
        }

        return rows;
    }

    // ===== 表ごとの読み替え =====

    /// <summary><c>show ap summary</c>。繋がっている AP が返る。</summary>
    public static IReadOnlyList<WlcApRow> ParseApSummary(string? text, string? joinText = null)
    {
        var rows = new List<WlcApRow>();

        foreach (ShowRow row in Table(text))
        {
            string name = row[ApNameHeads];
            if (name.Length == 0) continue;

            string state = row["state", "status", "ap state"];

            rows.Add(new WlcApRow(
                Name: name,
                State: DescribeApState(state, joined: true),
                StateKind: SeverityKind.Ok,
                IsJoined: true,
                Ip: Word(row[IpHeads]),
                Mac: Word(row[MacHeads]),
                Model: row[ModelHeads],
                Version: row["version", "sw version", "ap version"],
                Radios: row[SlotHeads],
                Clients: Number(row["clients", "client count", "# clients"]),
                ClientsText: Or(row["clients", "client count", "# clients"], "—"),
                Tags: row["policy tag", "site tag", "tag"]));
        }

        // 参加記録にしか出てこない AP（＝いま繋がっていない AP）も同じ表に混ぜる
        if (joinText is { Length: > 0 })
        {
            var known = new HashSet<string>(rows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

            foreach (WlcJoinRow join in ParseJoinStats(joinText))
            {
                if (join.Name.Length == 0 || known.Contains(join.Name)) continue;

                rows.Add(new WlcApRow(
                    Name: join.Name,
                    State: "✕ 未接続",
                    StateKind: SeverityKind.Alert,
                    IsJoined: false,
                    Ip: "",
                    Mac: join.Mac,
                    Model: "",
                    Version: "",
                    Radios: "",
                    Clients: 0,
                    ClientsText: "—",
                    Tags: ""));
            }
        }

        return rows;
    }

    /// <summary><c>show ap join stats summary</c>。参加と切断の最終値。</summary>
    public static IReadOnlyList<WlcJoinRow> ParseJoinStats(string? text)
    {
        var rows = new List<WlcJoinRow>();

        foreach (ShowRow row in Table(text))
        {
            string name = row[ApNameHeads];
            if (name.Length == 0) continue;

            string state = row["status", "state", "join status"];
            bool joined = state.Contains("joined", StringComparison.OrdinalIgnoreCase)
                          || state.Contains("run", StringComparison.OrdinalIgnoreCase)
                          || state.Contains("registered", StringComparison.OrdinalIgnoreCase);

            rows.Add(new WlcJoinRow(
                Name: name,
                Mac: Word(row[MacHeads]),
                State: DescribeApState(state, joined),
                StateKind: joined ? SeverityKind.Ok : SeverityKind.Alert,
                LastJoin: row["last successful join time", "last join time", "last successful join"],
                LastDisconnect: row["last disconnect time", "last disconnect"],
                Reason: row["last disconnect reason", "disconnect reason", "reason"],
                Joins: row["number of successful joins", "join count", "joins"],
                Failures: row["number of unsuccessful join attempts", "failed joins", "failures"]));
        }

        return rows;
    }

    /// <summary>
    /// <c>show wireless client summary</c>。
    /// <b>SSH では IP も電波の強さも出ない</b>ので、その欄は「—」のままにする
    /// （0 を入れて「強い」ように見せない）。SSID は WLAN の一覧から補う。
    /// </summary>
    public static IReadOnlyList<WlcClientRow> ParseClientSummary(
        string? text,
        IReadOnlyList<WlcSsidRow>? wlans = null,
        IReadOnlyDictionary<string, string>? ipByMac = null,
        Func<string, string>? vendorOf = null)
    {
        Dictionary<string, string> ssidById = new(StringComparer.OrdinalIgnoreCase);

        foreach (WlcSsidRow wlan in wlans ?? [])
        {
            if (wlan.Id.Length > 0 && wlan.Ssid.Length > 0) ssidById[wlan.Id] = wlan.Ssid;
        }

        var rows = new List<WlcClientRow>();

        foreach (ShowRow row in Table(text))
        {
            string mac = Word(row[MacHeads]);
            if (mac.Length == 0 || !mac.Contains('.', StringComparison.Ordinal)) continue;

            // 版によって見出しが「Type ID」で、中身が「WLAN 1」の形になる。数字だけ取る
            string wlanId = Digits(row["wlan id", "wlan", "id", "type id", "type"]);
            string state = row["state", "status"];
            bool up = state.Contains("run", StringComparison.OrdinalIgnoreCase);

            string ip = ipByMac is not null && ipByMac.TryGetValue(NormalizeMac(mac), out string? found)
                ? found
                : "";

            rows.Add(new WlcClientRow(
                Mac: mac,
                Ip: ip,
                Vendor: vendorOf?.Invoke(mac) ?? "",
                ApName: row[ApNameHeads],
                Ssid: ssidById.TryGetValue(wlanId, out string? ssid) ? ssid : row[SsidHeads],
                Radio: Word(row[ProtocolHeads]),
                Rssi: 0,
                RssiText: "—",
                Quality: "—",
                Snr: "",
                Speed: "",
                State: up ? "● 通信中" : Or(state, "—"),
                StateKind: up ? SeverityKind.Ok : SeverityKind.Muted,
                AssociatedAt: row["association time", "associated"]));
        }

        return rows;
    }

    /// <summary>
    /// <c>show wireless device-tracking database mac</c> / <c>… ip</c> から MAC → IP を作る。
    ///
    /// <b>見出し名は版で違う</b>ので当てにしない。<b>MAC に見える語と IPv4 に見える語</b>を
    /// 同じ行から拾う（端末の一覧に IP を埋めるためだけの表なので、これで足りる）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseIpBindings(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in Split(text))
        {
            string mac = "";
            string ip = "";

            foreach ((_, string word) in Words(line))
            {
                if (mac.Length == 0 && LooksLikeMac(word)) mac = word;
                else if (ip.Length == 0 && LooksLikeIpv4(word)) ip = word;
            }

            if (mac.Length > 0 && ip.Length > 0) map.TryAdd(NormalizeMac(mac), ip);
        }

        return map;
    }

    /// <summary>比べるための MAC（区切りを落とす）。表示は機器が返した形のまま。</summary>
    private static string NormalizeMac(string? mac)
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

    private static bool LooksLikeMac(string word)
        => NormalizeMac(word).Length == 12 && NormalizeMac(word).All(Uri.IsHexDigit)
           && (word.Contains('.', StringComparison.Ordinal)
               || word.Contains(':', StringComparison.Ordinal)
               || word.Contains('-', StringComparison.Ordinal));

    private static bool LooksLikeIpv4(string word)
        => System.Net.IPAddress.TryParse(word, out System.Net.IPAddress? address)
           && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    /// <summary><c>show wireless wlan summary</c>。SSID と WLAN 番号の対応もここから取る。</summary>
    public static IReadOnlyList<WlcSsidRow> ParseWlanSummary(string? text)
    {
        var rows = new List<WlcSsidRow>();

        foreach (ShowRow row in Table(text))
        {
            string id = row[IdHeads];
            string ssid = row[SsidHeads];

            if (id.Length == 0 && ssid.Length == 0) continue;

            string state = row["status", "state"];
            bool enabled = state.Contains("enabled", StringComparison.OrdinalIgnoreCase)
                           || state.Contains("up", StringComparison.OrdinalIgnoreCase);

            bool disabled = state.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                            || state.Contains("down", StringComparison.OrdinalIgnoreCase);

            rows.Add(new WlcSsidRow(
                Ssid: Or(ssid, row[ProfileHeads]),
                Profile: row[ProfileHeads],
                Id: id,
                // 知らない値はそのまま出す（言い換えて意味を変えない）
                State: enabled ? "● 有効" : disabled ? "◌ 無効" : Or(state, "—"),
                StateKind: enabled ? SeverityKind.Ok : SeverityKind.Muted,
                Clients: 0,
                Band24: 0,
                Band5: 0,
                Band6: 0));
        }

        return rows;
    }

    /// <summary>
    /// <c>show ap dot11 {24ghz|5ghz} summary</c>。
    /// <b>混み具合と雑音はこの出力に無い</b>ので「—」にする（<c>show ap auto-rf</c> は 1 台ずつで重い）。
    /// </summary>
    public static IReadOnlyList<WlcRrmRow> ParseRadioSummary(string? text, string radio)
    {
        var rows = new List<WlcRrmRow>();

        foreach (ShowRow row in Table(text))
        {
            string name = row[ApNameHeads];
            if (name.Length == 0) continue;

            rows.Add(new WlcRrmRow(
                ApName: name,
                Radio: radio,
                Channel: Word(row[ChannelHeads]),
                Power: row[PowerHeads],
                Utilization: -1,
                UtilizationText: "—",
                UtilizationKind: SeverityKind.Muted,
                Noise: "—",
                Clients: 0,
                ClientsText: "—"));
        }

        return rows;
    }

    /// <summary><c>show wireless wps rogue ap summary</c>。</summary>
    public static IReadOnlyList<WlcRogueRow> ParseRogueSummary(string? text)
    {
        var rows = new List<WlcRogueRow>();

        foreach (ShowRow row in Table(text))
        {
            string bssid = row["mac address", "bssid", "rogue ap mac"];
            if (bssid.Length == 0 || !bssid.Contains('.', StringComparison.Ordinal)) continue;

            string rssi = row["rssi", "max rssi", "strongest rssi"];

            rows.Add(new WlcRogueRow(
                Kind: Or(row["class type", "classification", "class"], "不正 AP"),
                Bssid: bssid,
                Vendor: "",
                Ssid: row[SsidHeads],
                Channel: Word(row[ChannelHeads]),
                Rssi: Number(rssi),
                RssiText: Or(rssi, "—"),
                DetectedBy: row["detecting ap", "detected by", "ap name"],
                LastHeard: row["last heard", "last seen"],
                Note: row["state", "status"]));
        }

        return rows;
    }

    // ===== 表の読み取り =====

    /// <summary>見出し行を探す。区切り線の 1 つ上、または語をいちばん多く含む行。</summary>
    private static int FindHeader(string[] lines)
    {
        for (int i = 1; i < lines.Length; i++)
        {
            if (IsSeparator(lines[i]) && lines[i - 1].Trim().Length > 0 && !IsSeparator(lines[i - 1]))
                return i - 1;
        }

        int best = -1, bestScore = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string lower = lines[i].ToLowerInvariant();
            int score = 0;

            foreach (string word in (string[])["ap name", "mac address", "ssid", "state", "status", "channel", "profile", "id"])
            {
                if (lower.Contains(word, StringComparison.Ordinal)) score++;
            }

            if (score > bestScore) (best, bestScore) = (i, score);
        }

        return bestScore >= 2 ? best : -1;
    }

    /// <summary>見出しの語と、その開始位置。</summary>
    private static (string Name, int Start)[] Columns(string header)
    {
        var columns = new List<(string, int)>();

        int i = 0;

        while (i < header.Length)
        {
            while (i < header.Length && header[i] == ' ') i++;
            if (i >= header.Length) break;

            int start = i;
            int end = i;

            // 語の中の 1 文字空白は同じ見出しの一部（"AP Name" / "Mac Address"）
            while (end < header.Length)
            {
                if (header[end] != ' ') { end++; continue; }
                if (end + 1 < header.Length && header[end + 1] != ' ') { end += 2; continue; }
                break;
            }

            columns.Add((header[start..Math.Min(end, header.Length)].Trim().ToLowerInvariant(), start));
            i = end;
        }

        return [.. columns];
    }

    /// <summary>
    /// 1 行を列に割る。
    ///
    /// <b>語に割ってから、見出しの位置でどの列かを決める。</b>
    /// 桁の切り方を「2 文字以上の空白」で決めると、
    /// <c>aabb.ccdd.eeff AP-1F-01</c> のように<b>空白 1 つで隣り合う値</b>が
    /// 1 つの語に化ける（2026-08-18 に「MAC の欄に AP 名まで入る」と報告された）。
    /// 位置で決めれば、値の中に空白があっても（<c>WLAN 1</c>）同じ列にまとまる。
    /// </summary>
    private static string[] Cells(string line, (string Name, int Start)[] columns)
    {
        string[] cells = new string[columns.Length];

        foreach ((int at, string word) in Words(line))
        {
            int column = ColumnAt(columns, at);

            cells[column] = cells[column] is { Length: > 0 } already ? already + " " + word : word;
        }

        return [.. cells.Select(c => c ?? "")];
    }

    /// <summary>
    /// 値の 1 語目だけを返す。
    ///
    /// <b>見出しの語が空白 1 つで隣り合っていると、2 つの列を 1 つとして読む</b>
    /// （<c>Protocol Method</c> がそれ。見出しからは切れ目が分からない）。
    /// 1 語で足りる欄（MAC・IP・電波の種別・チャンネル）は、そこから 1 語目を採る。
    /// </summary>
    private static string Word(string value)
    {
        int space = value.IndexOf(' ', StringComparison.Ordinal);

        return space < 0 ? value : value[..space];
    }

    /// <summary>その位置がどの列か。<b>いちばん近い見出しに寄せる</b>（桁は版で少しずれる）。</summary>
    private static int ColumnAt((string Name, int Start)[] columns, int at)
    {
        int best = 0;

        for (int c = 0; c < columns.Length; c++)
        {
            // 見出しの開始位置を過ぎていれば、その列の候補
            if (columns[c].Start <= at + 1) best = c;
            else break;
        }

        return best;
    }

    /// <summary>空白で区切った語と、その開始位置。</summary>
    private static IEnumerable<(int At, string Word)> Words(string line)
    {
        int i = 0;

        while (i < line.Length)
        {
            while (i < line.Length && line[i] == ' ') i++;
            if (i >= line.Length) break;

            int start = i;

            while (i < line.Length && line[i] != ' ') i++;

            yield return (start, line[start..i]);
        }
    }

    private static bool IsSeparator(string line)
    {
        string trimmed = line.Trim();

        return trimmed.Length >= 3 && trimmed.All(c => c is '-' or '=' or '_' or ' ');
    }

    /// <summary>表の前後に付いてくる行（件数・プロンプト・注記）。</summary>
    private static bool IsNoise(string line)
    {
        string trimmed = line.Trim();

        return trimmed.StartsWith("Number of", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Total", StringComparison.OrdinalIgnoreCase)
               || trimmed.EndsWith('#')
               || trimmed.EndsWith('>')
               || trimmed.StartsWith("show ", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] Split(string? text) => string.IsNullOrEmpty(text)
        ? []
        : [.. text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd())];

    /// <summary>
    /// AP の状態。<b>「Not Joined」を「joined を含む」で拾わないこと</b> —
    /// 打ち消しの語を先に見る（実際にここで落ちた）。
    /// </summary>
    private static string DescribeApState(string? state, bool joined)
    {
        if (string.IsNullOrWhiteSpace(state)) return joined ? "● 接続" : "✕ 未接続";

        if (state.Contains("not ", StringComparison.OrdinalIgnoreCase)
            || state.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || state.Contains("down", StringComparison.OrdinalIgnoreCase))
            return $"✕ {state}";

        if (state.Contains("registered", StringComparison.OrdinalIgnoreCase)
            || state.Contains("joined", StringComparison.OrdinalIgnoreCase)
            || state.Contains("run", StringComparison.OrdinalIgnoreCase))
            return "● 接続";

        return $"✕ {state}";
    }

    /// <summary>「WLAN 1」「1」から番号だけを取る。数字が無ければ空。</summary>
    private static string Digits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var digits = new string([.. text.Where(char.IsDigit)]);

        return digits;
    }

    private static int Number(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var digits = new string([.. text.Where(c => char.IsDigit(c) || c == '-')]);

        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }

    private static string Or(string first, string second) => first.Length > 0 ? first : second;
}
