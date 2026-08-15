using System.Text;
using System.Text.RegularExpressions;
using PingWatcher.Core.Reporting;

namespace PingWatcher.Core.Work;

/// <summary>変換できるコマンドの種類。</summary>
public enum CiscoCommandKind
{
    IpRoute,
    InterfaceBrief,
    CdpNeighbors,
    MacTable,
    InterfacesStatus,
    Inventory,
    Version,
    Logging,
}

/// <summary>変換結果の表。</summary>
public sealed record CsvTable(IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows)
{
    /// <summary>CSV 文字列にする。エスケープと数式無害化は既存の CSV 出力と同じ規則。</summary>
    public string ToCsv()
    {
        var csv = new StringBuilder();
        AppendRow(csv, Headers);
        foreach (string[] row in Rows)
            AppendRow(csv, row);
        return csv.ToString();
    }

    private static void AppendRow(StringBuilder csv, IReadOnlyList<string> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) csv.Append(',');
            csv.Append(CsvReportWriter.Quote(fields[i]));
        }

        // Excel 向けなので改行は CRLF に揃える
        csv.Append("\r\n");
    }
}

/// <summary>
/// Cisco コマンドの出力を CSV の表に変換する。貼り付けは英語 IOS 出力前提
/// （差分比較と同じ既定路線）。パーサは既存の 4 種(経路・interface brief・
/// CDP・MAC テーブル)を流用し、新規 4 種はこのファイル内で完結させる。
/// 読み取れない行は捏造せず落とす。
/// </summary>
public static partial class CiscoCsvConverter
{
    /// <summary>
    /// 貼り付けの種類を推定する。固有ヘッダを持つものから順に見て、
    /// 判定できなければ null(勝手に決めない)。
    /// Logging を最後に置くのは、%FAC-SEV-MNEM 行はターミナルログの混入として
    /// どの貼り付けにも紛れ得るため。
    /// </summary>
    public static CiscoCommandKind? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string[] lines = text.Split('\n');

        bool hasLineStart(string prefix)
            => lines.Any(l => l.TrimStart().StartsWith(prefix, StringComparison.Ordinal));

        if (hasLineStart("NAME:") && text.Contains("PID:", StringComparison.Ordinal))
            return CiscoCommandKind.Inventory;

        if (text.Contains("Cisco IOS Software", StringComparison.Ordinal)
            || text.Contains("Cisco IOS XE Software", StringComparison.Ordinal)
            || text.Contains("IOS (tm)", StringComparison.Ordinal))
            return CiscoCommandKind.Version;

        foreach (string line in lines)
        {
            if (line.Contains("Interface", StringComparison.Ordinal)
                && line.Contains("IP-Address", StringComparison.Ordinal)
                && line.Contains("OK?", StringComparison.Ordinal))
                return CiscoCommandKind.InterfaceBrief;

            if (line.Contains("Port", StringComparison.Ordinal)
                && line.Contains("Duplex", StringComparison.Ordinal)
                && line.Contains("Speed", StringComparison.Ordinal))
                return CiscoCommandKind.InterfacesStatus;

            if (line.Contains("Capability Codes:", StringComparison.Ordinal)
                || (line.Contains("Device ID", StringComparison.Ordinal)
                    && line.Contains("Local Intrfce", StringComparison.Ordinal)))
                return CiscoCommandKind.CdpNeighbors;
        }

        if (text.Contains("Mac Address Table", StringComparison.OrdinalIgnoreCase))
            return CiscoCommandKind.MacTable;

        if (hasLineStart("Codes:")
            || text.Contains("Gateway of last resort", StringComparison.Ordinal)
            || text.Contains("is directly connected", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"via \d+\.\d+\.\d+\.\d+"))
            return CiscoCommandKind.IpRoute;

        if (LogLine().IsMatch(text))
            return CiscoCommandKind.Logging;

        return null;
    }

    public static CsvTable Convert(CiscoCommandKind kind, string text) => kind switch
    {
        CiscoCommandKind.IpRoute => FromRoutes(text),
        CiscoCommandKind.InterfaceBrief => FromInterfaceBrief(text),
        CiscoCommandKind.CdpNeighbors => FromCdp(text),
        CiscoCommandKind.MacTable => FromMacTable(text),
        CiscoCommandKind.InterfacesStatus => FromInterfacesStatus(text),
        CiscoCommandKind.Inventory => FromInventory(text),
        CiscoCommandKind.Version => FromVersion(text),
        _ => FromLogging(text),
    };

    // ===== 既存パーサの写像 =====

    private static CsvTable FromRoutes(string text)
    {
        var rows = CiscoRouteParser.Parse(text)
            .Select(r => new[]
            {
                r.Prefix,
                r.Protocol,
                r.AdminDistance?.ToString() ?? "",
                r.Metric?.ToString() ?? "",
                string.Join(", ", r.NextHops),
                r.InterfaceText,
            })
            .ToList();

        return new CsvTable(["宛先", "プロトコル", "AD", "メトリック", "ネクストホップ", "インターフェース"], rows);
    }

    private static CsvTable FromInterfaceBrief(string text)
    {
        var rows = InterfaceBriefParser.Parse(text)
            .Select(e => new[] { e.Name, e.IpAddress, e.Status, e.Protocol })
            .ToList();

        return new CsvTable(["インターフェース", "IP", "Status", "Protocol"], rows);
    }

    private static CsvTable FromCdp(string text)
    {
        var rows = CdpNeighborParser.Parse(text)
            .Select(n => new[] { n.LocalInterface, n.DeviceId, n.Capability, n.Platform, n.RemotePort })
            .ToList();

        return new CsvTable(["自ポート", "機器名", "種別", "機種", "相手ポート"], rows);
    }

    private static CsvTable FromMacTable(string text)
    {
        var rows = MacTableParser.Parse(text)
            .Select(e => new[] { e.Vlan, e.Mac, e.Type, e.Port })
            .ToList();

        return new CsvTable(["VLAN", "MAC", "Type", "ポート"], rows);
    }

    // ===== show interfaces status(固定幅スライス) =====

    private static readonly string[] StatusColumns = ["Port", "Name", "Status", "Vlan", "Duplex", "Speed", "Type"];

    private static CsvTable FromInterfacesStatus(string text)
    {
        var rows = new List<string[]>();
        string[] lines = text.Split('\n');

        // ヘッダ行の各列見出しの開始位置で切る。IOS はこの表を桁で揃えて出す
        // (Name は空欄・途中切れがあるので、区切り文字では切れない)
        int headerIndex = -1;
        int[] starts = [];
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!line.TrimStart().StartsWith("Port", StringComparison.Ordinal)
                || !line.Contains("Duplex", StringComparison.Ordinal))
                continue;

            starts = new int[StatusColumns.Length];
            bool ok = true;
            int from = 0;
            for (int c = 0; c < StatusColumns.Length; c++)
            {
                int at = line.IndexOf(StatusColumns[c], from, StringComparison.Ordinal);
                if (at < 0) { ok = false; break; }
                starts[c] = at;
                from = at + StatusColumns[c].Length;
            }

            if (ok)
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0)
            return new CsvTable(["ポート", "Name", "Status", "Vlan", "Duplex", "Speed", "Type"], rows);

        for (int i = headerIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0)
                continue;

            var fields = new string[StatusColumns.Length];
            for (int c = 0; c < StatusColumns.Length; c++)
            {
                int start = Math.Min(starts[c], line.Length);
                int end = c + 1 < StatusColumns.Length ? Math.Min(starts[c + 1], line.Length) : line.Length;
                fields[c] = line[start..end].Trim();
            }

            // ポート名が無い行(続きのページヘッダなど)と罫線だけの行は表の行ではない
            if (fields[0].Length == 0 || fields[0].All(c => c == '-'))
                continue;

            rows.Add(fields);
        }

        return new CsvTable(["ポート", "Name", "Status", "Vlan", "Duplex", "Speed", "Type"], rows);
    }

    // ===== show inventory(NAME 行と PID 行のペア) =====

    [GeneratedRegex("""^NAME:\s*"(?<name>.*)"\s*,\s*DESCR:\s*"(?<descr>.*)"\s*$""")]
    private static partial Regex InventoryName();

    [GeneratedRegex(@"^PID:\s*(?<pid>\S*)\s*,\s*VID:\s*(?<vid>\S*)\s*,\s*SN:\s*(?<sn>.*)$")]
    private static partial Regex InventoryPid();

    private static CsvTable FromInventory(string text)
    {
        var rows = new List<string[]>();
        string? pendingName = null;
        string? pendingDescr = null;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();

            if (InventoryName().Match(line) is { Success: true } name)
            {
                pendingName = name.Groups["name"].Value;
                pendingDescr = name.Groups["descr"].Value;
                continue;
            }

            if (InventoryPid().Match(line) is { Success: true } pid && pendingName is not null)
            {
                rows.Add([pendingName, pendingDescr ?? "", pid.Groups["pid"].Value,
                    pid.Groups["vid"].Value, pid.Groups["sn"].Value.Trim()]);
                pendingName = null;
                pendingDescr = null;
            }
        }

        return new CsvTable(["NAME", "DESCR", "PID", "VID", "SN"], rows);
    }

    // ===== show version(取れた項目だけの縦持ち) =====

    private static CsvTable FromVersion(string text)
    {
        var rows = new List<string[]>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                rows.Add([label, value.Trim()]);
        }

        Match uptime = Regex.Match(text, @"^\s*(?<host>\S+) uptime is (?<up>.+)$", RegexOptions.Multiline);
        if (uptime.Success)
        {
            Add("ホスト名", uptime.Groups["host"].Value);
        }

        Match version = Regex.Match(text, @"(?:Cisco IOS.*?|IOS \(tm\).*?)Version (?<v>[^,\s\[]+)");
        Add("IOS バージョン", version.Success ? version.Groups["v"].Value : null);

        Match model = Regex.Match(text, @"Model Number\s*:\s*(?<m>\S+)", RegexOptions.IgnoreCase);
        if (!model.Success)
            model = Regex.Match(text, @"^\s*cisco (?<m>\S+) \(", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        Add("モデル", model.Success ? model.Groups["m"].Value : null);

        Match serial = Regex.Match(text, @"System Serial Number\s*:\s*(?<s>\S+)", RegexOptions.IgnoreCase);
        if (!serial.Success)
            serial = Regex.Match(text, @"Processor board ID (?<s>\S+)");
        Add("シリアル", serial.Success ? serial.Groups["s"].Value : null);

        if (uptime.Success)
            Add("uptime", uptime.Groups["up"].Value);

        Match image = Regex.Match(text, "System image file is \"(?<i>.+)\"");
        Add("System image", image.Success ? image.Groups["i"].Value : null);

        Match reload = Regex.Match(text, @"Last reload reason:\s*(?<r>.+)$", RegexOptions.Multiline);
        Add("再起動理由", reload.Success ? reload.Groups["r"].Value.TrimEnd('\r') : null);

        return new CsvTable(["項目", "値"], rows);
    }

    // ===== show logging(%FAC-SEV-MNEM 行の分解) =====

    [GeneratedRegex(@"^(?<prefix>.*?)%(?<fac>[A-Z0-9_]+)-(?<sev>[0-7])-(?<mn>[A-Z0-9_]+):\s?(?<msg>.*)$", RegexOptions.Multiline)]
    private static partial Regex LogLine();

    private static CsvTable FromLogging(string text)
    {
        var rows = new List<string[]>();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            Match match = LogLine().Match(line);
            if (!match.Success)
                continue;   // Syslog logging: などのヘッダ部は表の行ではない

            // 「% より前」を丸ごと時刻列に。*Mar 1 / seq 番号付き / 時刻なしのゆれを吸収する
            string prefix = match.Groups["prefix"].Value.Trim();
            if (prefix.EndsWith(':'))
                prefix = prefix[..^1].TrimEnd();

            rows.Add([prefix, match.Groups["fac"].Value, match.Groups["sev"].Value,
                match.Groups["mn"].Value, match.Groups["msg"].Value]);
        }

        return new CsvTable(["時刻", "ファシリティ", "重大度", "ニーモニック", "メッセージ"], rows);
    }
}
