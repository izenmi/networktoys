namespace NetworkToys.Core.Work;

/// <param name="Name">インターフェース名。</param>
/// <param name="IpAddress">IP アドレス（unassigned もそのまま入る）。</param>
/// <param name="Status">回線の状態（up / down / administratively down）。</param>
/// <param name="Protocol">プロトコルの状態（up / down）。</param>
public sealed record InterfaceBriefEntry(string Name, string IpAddress, string Status, string Protocol)
{
    public bool IsUp => string.Equals(Status, "up", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Protocol, "up", StringComparison.OrdinalIgnoreCase);

    /// <summary>意図的に落としてあるポートか。作業で落ちたのとは意味が違う。</summary>
    public bool IsAdministrativelyDown
        => Status.Contains("administratively", StringComparison.OrdinalIgnoreCase);

    public string StateText => $"{Status}/{Protocol}";
}

/// <summary>
/// <c>show ip interface brief</c> を読む。
///
/// <b>列を前から数えてはいけない。</b>Status には <c>administratively down</c> のように
/// 空白を含む値が入るため、単純な空白区切りだと列がずれて Protocol を取り違える。
/// 末尾から読む（最後が Protocol、その手前が Status で複数語になりうる）。
/// </summary>
public static class InterfaceBriefParser
{
    public static IReadOnlyList<InterfaceBriefEntry> Parse(string? text)
    {
        var entries = new List<InterfaceBriefEntry>();

        if (string.IsNullOrWhiteSpace(text))
            return entries;

        foreach (string rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = rawLine.Trim();

            if (line.Length == 0 || IsHeaderOrNoise(line))
                continue;

            string[] parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            // Interface / IP-Address / OK? / Method / Status... / Protocol
            if (parts.Length < 5)
                continue;

            string name = parts[0];
            string ip = parts[1];
            string protocol = parts[^1];

            // OK? と Method を飛ばした後ろから、Protocol の手前までが Status
            string status = string.Join(' ', parts[4..^1]);

            if (status.Length == 0)
                continue;

            entries.Add(new InterfaceBriefEntry(name, ip, status, protocol));
        }

        return entries;
    }

    private static bool IsHeaderOrNoise(string line)
        => line.StartsWith("Interface", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith('!')
           || line.Contains("Method Status", StringComparison.OrdinalIgnoreCase);
}

public static class InterfaceBriefDiff
{
    public static DeviceCompareOutcome Compare(string? beforeText, string? afterText)
    {
        IReadOnlyList<InterfaceBriefEntry> before = InterfaceBriefParser.Parse(beforeText);
        IReadOnlyList<InterfaceBriefEntry> after = InterfaceBriefParser.Parse(afterText);

        if (before.Count == 0 && after.Count == 0)
            return new DeviceCompareOutcome("インターフェースを読み取れませんでした。show ip interface brief の出力か確かめてください。", []);

        var beforeByName = ToLookup(before);
        var afterByName = ToLookup(after);
        var changes = new List<DeviceChange>();

        foreach ((string name, InterfaceBriefEntry entry) in beforeByName)
        {
            if (!afterByName.TryGetValue(name, out InterfaceBriefEntry? current))
            {
                changes.Add(DeviceChange.Warning(name, "消えた",
                    $"{name} が一覧から消えました（作業前: {entry.StateText}）。"));
                continue;
            }

            bool wasUp = entry.IsUp;
            bool isUp = current.IsUp;

            if (wasUp && !isUp)
            {
                // 意図的に落としたのか、落ちてしまったのかを言い分ける
                string reason = current.IsAdministrativelyDown ? "shutdown されています" : "リンクが落ちています";

                changes.Add(DeviceChange.Critical(name, "落ちた",
                    $"{name} が up から {current.StateText} になりました（{reason}）。"));
            }
            else if (!wasUp && isUp)
            {
                changes.Add(DeviceChange.Info(name, "上がった",
                    $"{name} が {entry.StateText} から up になりました。"));
            }
            else if (!string.Equals(entry.StateText, current.StateText, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(DeviceChange.Warning(name, "状態が変わった",
                    $"{name} の状態が {entry.StateText} から {current.StateText} に変わりました。"));
            }

            if (!string.Equals(entry.IpAddress, current.IpAddress, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(DeviceChange.Warning(name, "IP が変わった",
                    $"{name} の IP が {entry.IpAddress} から {current.IpAddress} に変わりました。"));
            }
        }

        foreach ((string name, InterfaceBriefEntry entry) in afterByName)
        {
            if (!beforeByName.ContainsKey(name))
            {
                changes.Add(DeviceChange.Info(name, "増えた",
                    $"{name} が増えました（{entry.StateText}）。"));
            }
        }

        int upBefore = before.Count(e => e.IsUp);
        int upAfter = after.Count(e => e.IsUp);

        string headline = changes.Count > 0
            ? $"インターフェース {before.Count} → {after.Count} 本／up は {upBefore} → {upAfter} 本"
            : $"インターフェース {after.Count} 本（up {upAfter} 本）、変化はありません";

        return new DeviceCompareOutcome(headline, DeviceCompareOutcome.Order(changes));
    }

    private static Dictionary<string, InterfaceBriefEntry> ToLookup(IReadOnlyList<InterfaceBriefEntry> entries)
    {
        var map = new Dictionary<string, InterfaceBriefEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (InterfaceBriefEntry entry in entries)
            map.TryAdd(entry.Name, entry);

        return map;
    }
}
