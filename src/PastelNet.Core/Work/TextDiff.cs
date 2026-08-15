using System.Text.RegularExpressions;

namespace PastelNet.Core.Work;

public enum DiffKind
{
    Unchanged,
    Removed,
    Added,
}

/// <param name="Kind">その行がどう変わったか。</param>
/// <param name="Text">行の内容。</param>
public sealed record DiffLine(DiffKind Kind, string Text);

public sealed record TextDiffResult(IReadOnlyList<DiffLine> Lines, int IgnoredLines, bool TooLarge)
{
    public bool HasChanges => Lines.Any(l => l.Kind != DiffKind.Unchanged);

    public int ChangedCount => Lines.Count(l => l.Kind != DiffKind.Unchanged);
}

/// <summary>
/// コマンド出力の前後を行単位で比べる。
///
/// そのまま差分を取ると<b>作業と無関係な行が大量に出て使い物にならない</b>ので、
/// 変動しやすい行を落とす仕組みを併せ持つ。
/// </summary>
public static class TextDiff
{
    /// <summary>これを超える行数は差分を諦める。</summary>
    private const int MaxLines = 5000;

    public static TextDiffResult Compare(string? before, string? after, DiffNoiseFilter? filter = null)
    {
        string[] beforeLines = Split(before);
        string[] afterLines = Split(after);

        if (beforeLines.Length > MaxLines || afterLines.Length > MaxLines)
            return new TextDiffResult([], 0, TooLarge: true);

        int ignored = 0;

        if (filter is not null)
        {
            int beforeCount = beforeLines.Length;
            int afterCount = afterLines.Length;

            beforeLines = filter.Apply(beforeLines);
            afterLines = filter.Apply(afterLines);

            ignored = (beforeCount - beforeLines.Length) + (afterCount - afterLines.Length);
        }

        var lines = new List<DiffLine>();

        // 前後の共通部分を先に削る。実際の差分はたいてい数行なので、
        // これだけで LCS に渡す量が一気に減る
        int head = 0;
        while (head < beforeLines.Length && head < afterLines.Length
               && string.Equals(beforeLines[head], afterLines[head], StringComparison.Ordinal))
        {
            head++;
        }

        int tail = 0;
        while (tail < beforeLines.Length - head && tail < afterLines.Length - head
               && string.Equals(
                   beforeLines[^(tail + 1)],
                   afterLines[^(tail + 1)],
                   StringComparison.Ordinal))
        {
            tail++;
        }

        for (int i = 0; i < head; i++)
            lines.Add(new DiffLine(DiffKind.Unchanged, beforeLines[i]));

        ReadOnlySpan<string> beforeMiddle = beforeLines.AsSpan(head, beforeLines.Length - head - tail);
        ReadOnlySpan<string> afterMiddle = afterLines.AsSpan(head, afterLines.Length - head - tail);

        lines.AddRange(DiffMiddle(beforeMiddle, afterMiddle));

        for (int i = beforeLines.Length - tail; i < beforeLines.Length; i++)
            lines.Add(new DiffLine(DiffKind.Unchanged, beforeLines[i]));

        return new TextDiffResult(lines, ignored, TooLarge: false);
    }

    /// <summary>差のある部分だけ最長共通部分列で並べる。</summary>
    private static List<DiffLine> DiffMiddle(ReadOnlySpan<string> before, ReadOnlySpan<string> after)
    {
        var result = new List<DiffLine>();

        if (before.Length == 0 && after.Length == 0)
            return result;

        if (before.Length == 0)
        {
            foreach (string line in after)
                result.Add(new DiffLine(DiffKind.Added, line));
            return result;
        }

        if (after.Length == 0)
        {
            foreach (string line in before)
                result.Add(new DiffLine(DiffKind.Removed, line));
            return result;
        }

        int[,] lengths = new int[before.Length + 1, after.Length + 1];

        for (int i = before.Length - 1; i >= 0; i--)
        {
            for (int j = after.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(before[i], after[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < before.Length && y < after.Length)
        {
            if (string.Equals(before[x], after[y], StringComparison.Ordinal))
            {
                result.Add(new DiffLine(DiffKind.Unchanged, before[x]));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                result.Add(new DiffLine(DiffKind.Removed, before[x]));
                x++;
            }
            else
            {
                result.Add(new DiffLine(DiffKind.Added, after[y]));
                y++;
            }
        }

        while (x < before.Length)
            result.Add(new DiffLine(DiffKind.Removed, before[x++]));

        while (y < after.Length)
            result.Add(new DiffLine(DiffKind.Added, after[y++]));

        return result;
    }

    private static string[] Split(string? text) => string.IsNullOrEmpty(text)
        ? []
        : [.. text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd())];
}

/// <summary>
/// 作業と関係なく毎回変わる行を落とす。
/// これが無いと差分がノイズで埋まり、本当の変化が見えなくなる。
/// </summary>
public sealed partial class DiffNoiseFilter
{
    private readonly string[] _contains;
    private readonly Func<string, string>? _normalize;

    private DiffNoiseFilter(string[] contains, Func<string, string>? normalize = null)
    {
        _contains = contains;
        _normalize = normalize;
    }

    /// <summary>
    /// ipconfig /all 用。
    /// DHCP のリース時刻と一時 IPv6 アドレスは、作業と無関係に必ず変わる。
    /// 表示言語で文言が変わるので、日本語と英語の両方を持つ。
    /// </summary>
    public static DiffNoiseFilter IpConfig { get; } = new(
    [
        "リースが取得された日",
        "リースの有効期限",
        "Lease Obtained",
        "Lease Expires",
        "一時 IPv6 アドレス",
        "Temporary IPv6 Address",
    ]);

    /// <summary>
    /// route print 用。
    /// メトリックはリンク速度や状態で動くので、<b>リンクが一瞬落ちただけで全行が差分になる</b>。
    /// 行を落とすのではなく、メトリック列だけを取り除いて比べる。
    /// </summary>
    public static DiffNoiseFilter RouteTable { get; } = new([], NormalizeRouteLine);

    public string[] Apply(string[] lines)
    {
        var result = new List<string>(lines.Length);

        foreach (string line in lines)
        {
            if (_contains.Any(c => line.Contains(c, StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(_normalize is null ? line : _normalize(line));
        }

        return [.. result];
    }

    /// <summary>経路表の行から末尾のメトリックを落とす。IPv4 の行だけを対象にする。</summary>
    private static string NormalizeRouteLine(string line)
    {
        Match match = RouteLine().Match(line);
        return match.Success ? match.Groups[1].Value.TrimEnd() : line;
    }

    [GeneratedRegex(@"^(\s*\d{1,3}(?:\.\d{1,3}){3}\s+.*?)\s+\d+\s*$")]
    private static partial Regex RouteLine();
}
