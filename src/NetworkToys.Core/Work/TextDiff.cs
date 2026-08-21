using System.Text.RegularExpressions;

namespace NetworkToys.Core.Work;

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
    /// <summary>
    /// これを超える行数は差分を諦める。
    ///
    /// <b>2 万行の設定を比べたい</b>という用途があるので広く取ってある（2026-08-17 ユーザー指示）。
    /// 行数そのものは重くない — 重いのは下の LCS の表なので、そちらは
    /// <see cref="Anchored"/> が小さく割ってから使う。
    /// </summary>
    private const int MaxLines = 200_000;

    /// <summary>
    /// LCS の表に許す面積（セル数）。ここを超える区間は、
    /// <b>一意な共通行で区切ってから</b>小さく割って解く（patience diff）。
    /// それでも割れない区間は「まるごと削除 → まるごと追加」で見せる。
    /// 2,000,000 セルなら表は約 8MB で収まる。
    /// </summary>
    private const long MaxCells = 2_000_000;

    /// <summary>区切りの入れ子の深さの上限。ここを超えたら割るのをやめる。</summary>
    private const int MaxDepth = 32;

    public static TextDiffResult Compare(string? before, string? after, DiffNoiseFilter? filter = null)
    {
        string[] beforeLines = Split(before);
        string[] afterLines = Split(after);

        int ignored = 0;

        // ノイズ除去を先に掛ける。行数の判定を先にすると、
        // 除去すれば上限に収まる入力まで諦めてしまう
        if (filter is not null)
        {
            int beforeCount = beforeLines.Length;
            int afterCount = afterLines.Length;

            beforeLines = filter.Apply(beforeLines);
            afterLines = filter.Apply(afterLines);

            ignored = (beforeCount - beforeLines.Length) + (afterCount - afterLines.Length);
        }

        if (beforeLines.Length > MaxLines || afterLines.Length > MaxLines)
            return new TextDiffResult([], ignored, TooLarge: true);

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

        ReadOnlySpan<string> beforeMiddle = beforeLines.AsSpan(head, beforeLines.Length - head - tail);
        ReadOnlySpan<string> afterMiddle = afterLines.AsSpan(head, afterLines.Length - head - tail);

        var lines = new List<DiffLine>();

        for (int i = 0; i < head; i++)
            lines.Add(new DiffLine(DiffKind.Unchanged, beforeLines[i]));

        lines.AddRange(DiffMiddle(beforeMiddle, afterMiddle));

        for (int i = beforeLines.Length - tail; i < beforeLines.Length; i++)
            lines.Add(new DiffLine(DiffKind.Unchanged, beforeLines[i]));

        return new TextDiffResult(lines, ignored, TooLarge: false);
    }

    /// <summary>差のある部分を並べる。大きい区間は一意な共通行で割ってから解く。</summary>
    private static List<DiffLine> DiffMiddle(ReadOnlySpan<string> before, ReadOnlySpan<string> after)
    {
        var result = new List<DiffLine>();
        Fill(before, after, 0, result);
        return result;
    }

    /// <summary>
    /// 区間を 1 つ解いて <paramref name="result"/> の末尾へ足す。
    ///
    /// <b>設定ファイルは同じ行がほとんど</b>なので、まず前後の共通部分を削り、
    /// それでも大きければ<b>両方に 1 回ずつしか出てこない行</b>を目印にして割る。
    /// 目印は必ず対応が付くので、そこで切っても差分の質は落ちない。
    /// </summary>
    private static void Fill(
        ReadOnlySpan<string> before, ReadOnlySpan<string> after, int depth, List<DiffLine> result)
    {
        // 前後の共通部分（区間ごとにも効く。割ったあとの端はたいてい揃っている）
        int head = 0;
        while (head < before.Length && head < after.Length
               && string.Equals(before[head], after[head], StringComparison.Ordinal))
        {
            result.Add(new DiffLine(DiffKind.Unchanged, before[head]));
            head++;
        }

        before = before[head..];
        after = after[head..];

        int tail = 0;
        while (tail < before.Length && tail < after.Length
               && string.Equals(before[^(tail + 1)], after[^(tail + 1)], StringComparison.Ordinal))
        {
            tail++;
        }

        ReadOnlySpan<string> tailLines = before[(before.Length - tail)..];
        before = before[..(before.Length - tail)];
        after = after[..(after.Length - tail)];

        if (before.Length == 0)
        {
            foreach (string line in after)
                result.Add(new DiffLine(DiffKind.Added, line));
        }
        else if (after.Length == 0)
        {
            foreach (string line in before)
                result.Add(new DiffLine(DiffKind.Removed, line));
        }
        else if ((long)before.Length * after.Length <= MaxCells)
        {
            result.AddRange(Lcs(before, after));
        }
        else if (depth < MaxDepth && Anchored(before, after, depth, result))
        {
            // Anchored が中で足した
        }
        else
        {
            // 目印が 1 つも無い（総入れ替え）。行ごとの対応は付けようがないので、
            // まるごと差し替えとして見せる — 諦めて何も出さないより読める
            foreach (string line in before)
                result.Add(new DiffLine(DiffKind.Removed, line));

            foreach (string line in after)
                result.Add(new DiffLine(DiffKind.Added, line));
        }

        foreach (string line in tailLines)
            result.Add(new DiffLine(DiffKind.Unchanged, line));
    }

    /// <summary>
    /// 両方に 1 回ずつしか出てこない行を目印にして区間を割る（patience diff）。
    /// 目印が取れなければ <c>false</c>。
    /// </summary>
    private static bool Anchored(
        ReadOnlySpan<string> before, ReadOnlySpan<string> after, int depth, List<DiffLine> result)
    {
        Dictionary<string, int> beforeCount = new(StringComparer.Ordinal);
        Dictionary<string, int> afterCount = new(StringComparer.Ordinal);
        Dictionary<string, int> beforeAt = new(StringComparer.Ordinal);
        Dictionary<string, int> afterAt = new(StringComparer.Ordinal);

        for (int i = 0; i < before.Length; i++)
        {
            beforeCount[before[i]] = beforeCount.GetValueOrDefault(before[i]) + 1;
            beforeAt[before[i]] = i;
        }

        for (int i = 0; i < after.Length; i++)
        {
            afterCount[after[i]] = afterCount.GetValueOrDefault(after[i]) + 1;
            afterAt[after[i]] = i;
        }

        // after の並び順に、一意な共通行を拾う
        var pairs = new List<(int Before, int After)>();

        for (int i = 0; i < after.Length; i++)
        {
            string line = after[i];

            if (afterCount[line] == 1 && beforeCount.GetValueOrDefault(line) == 1)
                pairs.Add((beforeAt[line], i));
        }

        if (pairs.Count == 0) return false;

        // 目印どうしが交差していると割れないので、before 側が増える並びだけ残す
        int[] anchors = LongestIncreasing([.. pairs.Select(p => p.Before)]);

        if (anchors.Length == 0) return false;

        int x = 0, y = 0;

        foreach (int index in anchors)
        {
            (int beforeAtIndex, int afterAtIndex) = pairs[index];

            Fill(before[x..beforeAtIndex], after[y..afterAtIndex], depth + 1, result);

            result.Add(new DiffLine(DiffKind.Unchanged, before[beforeAtIndex]));

            x = beforeAtIndex + 1;
            y = afterAtIndex + 1;
        }

        Fill(before[x..], after[y..], depth + 1, result);

        return true;
    }

    /// <summary>最長増加部分列の<b>添字</b>を返す。目印を交差しない形に間引くために使う。</summary>
    private static int[] LongestIncreasing(int[] values)
    {
        if (values.Length == 0) return [];

        var tails = new List<int>();          // tails[k] = 長さ k+1 の列の末尾の添字
        int[] previous = new int[values.Length];

        for (int i = 0; i < values.Length; i++)
        {
            int low = 0, high = tails.Count;

            while (low < high)
            {
                int mid = (low + high) / 2;

                if (values[tails[mid]] < values[i]) low = mid + 1;
                else high = mid;
            }

            previous[i] = low > 0 ? tails[low - 1] : -1;

            if (low == tails.Count) tails.Add(i);
            else tails[low] = i;
        }

        var result = new int[tails.Count];

        for (int i = tails.Count - 1, at = tails[^1]; i >= 0; i--, at = previous[at])
            result[i] = at;

        return result;
    }

    /// <summary>小さい区間を最長共通部分列で並べる。</summary>
    private static List<DiffLine> Lcs(ReadOnlySpan<string> before, ReadOnlySpan<string> after)
    {
        var result = new List<DiffLine>();

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

    /// <summary>
    /// Cisco の <c>show ip route</c> 用。
    /// 経過時間(<c>00:12:34</c> / <c>2d05h</c> / <c>1w2d</c> / <c>1y2w</c>)は作業と無関係に
    /// 毎回変わるので、行を落とさず時間だけを取り除いて比べる。
    /// 上段の「経路として見た変化」は構造化(CiscoRouteParser)側が時間を読み捨てるが、
    /// 下段の行差分はこちらで守る(2026-08-21 ユーザー報告)。
    /// </summary>
    public static DiffNoiseFilter CiscoRoutes { get; } = new([], NormalizeCiscoRouteLine);

    /// <summary>
    /// Cisco の <c>show run</c> 用。
    /// 設定を何も変えていなくても、取得のたびに必ず変わる行がある。
    /// </summary>
    public static DiffNoiseFilter CiscoConfig { get; } = new(
    [
        "Building configuration",
        "Current configuration :",
        "Last configuration change",
        "NVRAM config last updated",
        "ntp clock-period",
    ]);

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

    /// <summary>「, 00:12:34」のような経過時間の節を取り除く(次が区切りか行末のときだけ)。</summary>
    private static string NormalizeCiscoRouteLine(string line) => CiscoRouteAge().Replace(line, "");

    [GeneratedRegex(@",\s*(?:\d{1,2}:\d{2}:\d{2}|\d+y\d+w|\d+w\d+d|\d+d\d{2}h)(?=,|\s*$)")]
    private static partial Regex CiscoRouteAge();
}
