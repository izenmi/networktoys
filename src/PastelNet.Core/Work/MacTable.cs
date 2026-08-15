using System.Text.RegularExpressions;

namespace PastelNet.Core.Work;

/// <param name="Vlan">VLAN 番号。</param>
/// <param name="Mac">MAC アドレス（0011.2233.4455 の形のまま）。</param>
/// <param name="Type">DYNAMIC / STATIC。</param>
/// <param name="Port">見えているポート。</param>
public sealed record MacTableEntry(string Vlan, string Mac, string Type, string Port)
{
    public string Key => $"{Vlan}|{Mac}".ToUpperInvariant();

    public bool IsDynamic => Type.Contains("DYNAMIC", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// <c>show mac address-table</c> を読む。
/// </summary>
public static partial class MacTableParser
{
    public static IReadOnlyList<MacTableEntry> Parse(string? text)
    {
        var entries = new List<MacTableEntry>();

        if (string.IsNullOrWhiteSpace(text))
            return entries;

        foreach (string rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = rawLine.Trim();

            if (line.Length == 0 || !MacAddress().IsMatch(line))
                continue;

            string[] parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            // Vlan / Mac Address / Type / Ports
            if (parts.Length < 4)
                continue;

            int macIndex = Array.FindIndex(parts, p => MacAddress().IsMatch(p));
            if (macIndex < 1 || macIndex + 2 >= parts.Length)
                continue;

            entries.Add(new MacTableEntry(
                parts[macIndex - 1],
                parts[macIndex].ToUpperInvariant(),
                parts[macIndex + 1],
                // ポート名は末尾。複数ポートに見えることもあるのでまとめて拾う
                string.Join(' ', parts[(macIndex + 2)..])));
        }

        return entries;
    }

    // 0011.2233.4455 / 00:11:22:33:44:55 / 00-11-22-33-44-55
    [GeneratedRegex(@"(?:[0-9a-fA-F]{4}\.){2}[0-9a-fA-F]{4}|(?:[0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}")]
    private static partial Regex MacAddress();
}

public static class MacTableDiff
{
    public static DeviceCompareOutcome Compare(string? beforeText, string? afterText)
    {
        IReadOnlyList<MacTableEntry> before = MacTableParser.Parse(beforeText);
        IReadOnlyList<MacTableEntry> after = MacTableParser.Parse(afterText);

        if (before.Count == 0 && after.Count == 0)
            return new DeviceCompareOutcome("MAC アドレステーブルを読み取れませんでした。show mac address-table の出力か確かめてください。", []);

        var beforeByKey = ToLookup(before);
        var afterByKey = ToLookup(after);
        var changes = new List<DeviceChange>();

        foreach ((string key, MacTableEntry entry) in beforeByKey)
        {
            if (!afterByKey.TryGetValue(key, out MacTableEntry? current))
            {
                // 動的エントリはエージングで勝手に消えるので、消えたこと自体は軽く扱う
                ChangeSeverity severity = entry.IsDynamic ? ChangeSeverity.Info : ChangeSeverity.Warning;

                changes.Add(new DeviceChange(entry.Mac, "消えた",
                    $"{entry.Mac}（VLAN {entry.Vlan}、{entry.Port}）が見えなくなりました。",
                    severity));
                continue;
            }

            if (!string.Equals(entry.Port, current.Port, StringComparison.OrdinalIgnoreCase))
            {
                // これが見たいもの。機器の差し替えやループの兆候
                changes.Add(DeviceChange.Critical(entry.Mac, "ポートが移った",
                    $"{entry.Mac}（VLAN {entry.Vlan}）が {entry.Port} から {current.Port} に移りました。"));
            }
        }

        foreach ((string key, MacTableEntry entry) in afterByKey)
        {
            if (!beforeByKey.ContainsKey(key))
            {
                ChangeSeverity severity = entry.IsDynamic ? ChangeSeverity.Info : ChangeSeverity.Warning;

                changes.Add(new DeviceChange(entry.Mac, "現れた",
                    $"{entry.Mac}（VLAN {entry.Vlan}）が {entry.Port} に現れました。",
                    severity));
            }
        }

        int moved = changes.Count(c => c.Kind == "ポートが移った");

        string headline = moved > 0
            ? $"MAC {before.Count} → {after.Count} 件／{moved} 件が別のポートに移りました"
            : $"MAC {before.Count} → {after.Count} 件、ポートの移動はありません";

        return new DeviceCompareOutcome(
            headline,
            DeviceCompareOutcome.Order(changes),
            "動的エントリは通信が無いと数分で消えるため、増減は作業と関係なく起きます。見るべきは「ポートが移った」項目です。");
    }

    private static Dictionary<string, MacTableEntry> ToLookup(IReadOnlyList<MacTableEntry> entries)
    {
        var map = new Dictionary<string, MacTableEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (MacTableEntry entry in entries)
            map.TryAdd(entry.Key, entry);

        return map;
    }
}
