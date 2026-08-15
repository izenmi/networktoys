namespace PingWatcher.Core.Work;

/// <summary>左右に並べたときの 1 行の意味。</summary>
public enum SideKind
{
    /// <summary>両側とも同じ。</summary>
    Same,

    /// <summary>書き換わった（左右とも中身がある）。</summary>
    Changed,

    /// <summary>作業前にだけあった。</summary>
    LeftOnly,

    /// <summary>作業後にだけある。</summary>
    RightOnly,
}

/// <param name="LeftNumber">作業前の行番号。無ければ null。</param>
/// <param name="Left">作業前の内容。</param>
/// <param name="RightNumber">作業後の行番号。</param>
/// <param name="Right">作業後の内容。</param>
/// <param name="Kind">どう変わったか。</param>
public sealed record SideBySideRow(
    int? LeftNumber,
    string? Left,
    int? RightNumber,
    string? Right,
    SideKind Kind)
{
    public string LeftText => Left ?? string.Empty;

    public string RightText => Right ?? string.Empty;

    public string LeftNumberText => LeftNumber?.ToString() ?? string.Empty;

    public string RightNumberText => RightNumber?.ToString() ?? string.Empty;

    public bool IsDifferent => Kind != SideKind.Same;

    /// <summary>
    /// 行内の色分け。書き換わった行だけ「どこが違うか」を区切って返し、
    /// それ以外は行全体を 1 区切りで返す(描画側はどの行も同じ手順で描ける)。
    /// </summary>
    public IReadOnlyList<DiffSegment> LeftSegments
        => Kind == SideKind.Changed ? IntralineDiff.Split(LeftText, RightText).Left : [new DiffSegment(LeftText, false)];

    public IReadOnlyList<DiffSegment> RightSegments
        => Kind == SideKind.Changed ? IntralineDiff.Split(LeftText, RightText).Right : [new DiffSegment(RightText, false)];
}

public sealed record SideBySideResult(IReadOnlyList<SideBySideRow> Rows, int IgnoredLines, bool TooLarge)
{
    public int ChangedCount => Rows.Count(r => r.IsDifferent);

    public bool HasChanges => ChangedCount > 0;
}

/// <summary>
/// 左右に並べて見比べる形の差分を組み立てる。
///
/// 消えた行と現れた行が続いているときは<b>同じ行の書き換え</b>として左右に並べる。
/// 上下に離して出すより、どこが書き換わったのかが目で追いやすい。
/// </summary>
public static class SideBySideDiff
{
    public static SideBySideResult Build(string? before, string? after, DiffNoiseFilter? filter = null)
    {
        TextDiffResult diff = TextDiff.Compare(before, after, filter);

        if (diff.TooLarge)
            return new SideBySideResult([], diff.IgnoredLines, TooLarge: true);

        var rows = new List<SideBySideRow>(diff.Lines.Count);
        int leftNumber = 0;
        int rightNumber = 0;
        int index = 0;

        while (index < diff.Lines.Count)
        {
            DiffLine line = diff.Lines[index];

            if (line.Kind == DiffKind.Unchanged)
            {
                leftNumber++;
                rightNumber++;
                rows.Add(new SideBySideRow(leftNumber, line.Text, rightNumber, line.Text, SideKind.Same));
                index++;
                continue;
            }

            // 続いている「消えた行」と「現れた行」をまとめて拾い、突き合わせる
            var removed = new List<string>();
            while (index < diff.Lines.Count && diff.Lines[index].Kind == DiffKind.Removed)
                removed.Add(diff.Lines[index++].Text);

            var added = new List<string>();
            while (index < diff.Lines.Count && diff.Lines[index].Kind == DiffKind.Added)
                added.Add(diff.Lines[index++].Text);

            int pairs = Math.Max(removed.Count, added.Count);

            for (int i = 0; i < pairs; i++)
            {
                string? left = i < removed.Count ? removed[i] : null;
                string? right = i < added.Count ? added[i] : null;

                int? leftAt = null;
                int? rightAt = null;

                if (left is not null) leftAt = ++leftNumber;
                if (right is not null) rightAt = ++rightNumber;

                SideKind kind = (left, right) switch
                {
                    (not null, not null) => SideKind.Changed,
                    (not null, null) => SideKind.LeftOnly,
                    _ => SideKind.RightOnly,
                };

                rows.Add(new SideBySideRow(leftAt, left, rightAt, right, kind));
            }
        }

        return new SideBySideResult(rows, diff.IgnoredLines, TooLarge: false);
    }

    /// <summary>差のある行だけを抜き出す。件数が多いときに変化した箇所だけ見たい場合に使う。</summary>
    public static IReadOnlyList<SideBySideRow> OnlyDifferences(SideBySideResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return [.. result.Rows.Where(r => r.IsDifferent)];
    }
}
