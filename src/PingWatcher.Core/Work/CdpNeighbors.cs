using System.Text.RegularExpressions;

namespace PingWatcher.Core.Work;

/// <param name="DeviceId">隣接機器の名前。</param>
/// <param name="LocalInterface">自分側のポート。</param>
/// <param name="Capability">機器の種別（R S I など）。</param>
/// <param name="Platform">機種。</param>
/// <param name="RemotePort">相手側のポート。</param>
public sealed record CdpNeighbor(
    string DeviceId,
    string LocalInterface,
    string Capability,
    string Platform,
    string RemotePort)
{
    public string LinkText => $"{LocalInterface} ⇔ {DeviceId} {RemotePort}";
}

/// <summary>
/// <c>show cdp neighbors</c> を読む。
///
/// <b>Holdtime は必ず捨てる。</b>数秒ごとに減っていく値なので、残したままだと
/// 何も変えていなくても全行が差分になる（経路表の経過時間と同じ理由）。
///
/// 機器名が長いと次の行へ折り返すことがあるので、その形にも備える。
/// </summary>
public static partial class CdpNeighborParser
{
    public static IReadOnlyList<CdpNeighbor> Parse(string? text)
    {
        var neighbors = new List<CdpNeighbor>();

        if (string.IsNullOrWhiteSpace(text))
            return neighbors;

        string? pendingDeviceId = null;

        foreach (string rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = rawLine.TrimEnd();

            if (line.Trim().Length == 0 || IsHeaderOrNoise(line))
                continue;

            // 機器名だけの行。次の行に残りが続く
            if (DeviceIdOnly().IsMatch(line))
            {
                pendingDeviceId = line.Trim();
                continue;
            }

            string[] parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            // 折り返しの続き（機器名が前の行にある）
            if (pendingDeviceId is not null)
            {
                if (TryBuild(pendingDeviceId, parts, fromContinuation: true) is { } continued)
                    neighbors.Add(continued);

                pendingDeviceId = null;
                continue;
            }

            if (parts.Length < 5)
                continue;

            if (TryBuild(parts[0], parts[1..], fromContinuation: false) is { } neighbor)
                neighbors.Add(neighbor);
        }

        return neighbors;
    }

    /// <summary>
    /// 機器名を除いた残り（自分側ポート / Holdtime / 種別 / 機種 / 相手ポート）を読む。
    ///
    /// ポート名は "Gig 0/1" のように空白で割れることがあるので、
    /// <b>Holdtime（数字だけのトークン）を境目にして前後を分ける</b>。
    /// </summary>
    private static CdpNeighbor? TryBuild(string deviceId, string[] rest, bool fromContinuation)
    {
        if (rest.Length < 4)
            return null;

        int holdtime = Array.FindIndex(rest, IsNumeric);

        if (holdtime <= 0 || holdtime >= rest.Length - 1)
            return null;

        // Holdtime より前が自分側のポート
        string localInterface = string.Join(' ', rest[..holdtime]);

        // 相手側のポートは末尾。"Gig 0/24" のように 2 語になることがある
        string[] tail = rest[(holdtime + 1)..];

        if (tail.Length < 2)
            return null;

        int remoteStart = tail.Length >= 2 && IsPortWord(tail[^2]) ? tail.Length - 2 : tail.Length - 1;
        string remotePort = string.Join(' ', tail[remoteStart..]);

        string[] middle = tail[..remoteStart];

        // 種別と機種。機種は空白を含みうるので、先頭 1〜2 語を種別とみなす
        string capability = middle.Length > 0 ? middle[0] : string.Empty;
        string platform = middle.Length > 1 ? string.Join(' ', middle[1..]) : string.Empty;

        _ = fromContinuation;

        return new CdpNeighbor(deviceId, localInterface, capability, platform, remotePort);
    }

    private static bool IsNumeric(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    /// <summary>"Gig" や "Fas" のような、数字を含まないポートの頭。</summary>
    private static bool IsPortWord(string value)
        => value.Length > 0 && value.All(char.IsAsciiLetter);

    private static bool IsHeaderOrNoise(string line)
    {
        string trimmed = line.Trim();

        return trimmed.StartsWith("Device ID", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Capability Codes", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Total cdp entries", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith('!')
               // 凡例の続き（"S - Switch, H - Host" など）
               || (trimmed.Contains(" - ", StringComparison.Ordinal) && trimmed.Contains(',', StringComparison.Ordinal)
                   && !trimmed.Contains('/', StringComparison.Ordinal));
    }

    // 行に機器名しか無い（折り返しの1行目）
    [GeneratedRegex(@"^\S+\s*$")]
    private static partial Regex DeviceIdOnly();
}

public static class CdpNeighborDiff
{
    public static DeviceCompareOutcome Compare(string? beforeText, string? afterText)
    {
        IReadOnlyList<CdpNeighbor> before = CdpNeighborParser.Parse(beforeText);
        IReadOnlyList<CdpNeighbor> after = CdpNeighborParser.Parse(afterText);

        if (before.Count == 0 && after.Count == 0)
            return new DeviceCompareOutcome("隣接機器を読み取れませんでした。show cdp neighbors の出力か確かめてください。", []);

        // 自分側のポートを鍵にする。「このポートの先に何がいるか」が知りたいので
        var beforeByPort = ToLookup(before);
        var afterByPort = ToLookup(after);
        var changes = new List<DeviceChange>();

        foreach ((string port, CdpNeighbor entry) in beforeByPort)
        {
            if (!afterByPort.TryGetValue(port, out CdpNeighbor? current))
            {
                changes.Add(DeviceChange.Critical(port, "いなくなった",
                    $"{port} の先にいた {entry.DeviceId}（{entry.RemotePort}）が見えなくなりました。"));
                continue;
            }

            if (!string.Equals(entry.DeviceId, current.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(DeviceChange.Critical(port, "相手が変わった",
                    $"{port} の先が {entry.DeviceId} から {current.DeviceId} に変わりました。"));
                continue;
            }

            if (!string.Equals(entry.RemotePort, current.RemotePort, StringComparison.OrdinalIgnoreCase))
            {
                // ここが cdp を見る一番の理由。挿し直したポートが違っている
                changes.Add(DeviceChange.Critical(port, "挿し口が変わった",
                    $"{port} の接続先が {entry.DeviceId} の {entry.RemotePort} から {current.RemotePort} に変わりました。挿し間違いの可能性があります。"));
            }
        }

        foreach ((string port, CdpNeighbor entry) in afterByPort)
        {
            if (!beforeByPort.ContainsKey(port))
            {
                changes.Add(DeviceChange.Warning(port, "現れた",
                    $"{port} の先に {entry.DeviceId}（{entry.RemotePort}）が見えるようになりました。"));
            }
        }

        string headline = changes.Count > 0
            ? $"隣接機器 {before.Count} → {after.Count} 台"
            : $"隣接機器 {after.Count} 台、変化はありません";

        return new DeviceCompareOutcome(
            headline,
            DeviceCompareOutcome.Order(changes),
            "CDP は数十秒ごとに更新されるため、機器を再起動した直後は一時的に見えないことがあります。");
    }

    private static Dictionary<string, CdpNeighbor> ToLookup(IReadOnlyList<CdpNeighbor> neighbors)
    {
        var map = new Dictionary<string, CdpNeighbor>(StringComparer.OrdinalIgnoreCase);

        foreach (CdpNeighbor neighbor in neighbors)
            map.TryAdd(Normalize(neighbor.LocalInterface), neighbor);

        return map;
    }

    /// <summary>"Gig 0/1" と "Gig0/1" を同じものとして扱う。</summary>
    private static string Normalize(string port) => port.Replace(" ", string.Empty, StringComparison.Ordinal);
}
