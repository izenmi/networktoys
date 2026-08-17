namespace NetworkToys.Core.Work;

/// <summary>行の中の一区切り。Changed の部分だけ色を変えて描く。</summary>
public readonly record struct DiffSegment(string Text, bool Changed);

/// <summary>
/// 書き換わった行の「行の中のどこが違うか」。WinMerge の要領で、
/// 共通の前置きと後置きを外した残りを「違う箇所」として塗る。
/// 文字単位の LCS までは踏み込まない(設定行の書き換えは
/// 値の部分だけが変わることがほとんどで、前後一致で十分読める)。
/// </summary>
public static class IntralineDiff
{
    public static (IReadOnlyList<DiffSegment> Left, IReadOnlyList<DiffSegment> Right) Split(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
            return (Whole(left, changed: false), Whole(right, changed: false));

        int prefix = 0;
        int max = Math.Min(left.Length, right.Length);
        while (prefix < max && left[prefix] == right[prefix])
            prefix++;

        // 後置きは前置きと重ならない範囲で探す
        int suffix = 0;
        while (suffix < max - prefix
               && left[left.Length - 1 - suffix] == right[right.Length - 1 - suffix])
            suffix++;

        return (Build(left, prefix, suffix), Build(right, prefix, suffix));
    }

    private static IReadOnlyList<DiffSegment> Build(string text, int prefix, int suffix)
    {
        var segments = new List<DiffSegment>(3);

        if (prefix > 0)
            segments.Add(new DiffSegment(text[..prefix], Changed: false));

        int middle = text.Length - prefix - suffix;
        if (middle > 0)
            segments.Add(new DiffSegment(text.Substring(prefix, middle), Changed: true));

        if (suffix > 0)
            segments.Add(new DiffSegment(text[^suffix..], Changed: false));

        if (segments.Count == 0)
            segments.Add(new DiffSegment(string.Empty, Changed: false));

        return segments;
    }

    private static IReadOnlyList<DiffSegment> Whole(string text, bool changed)
        => [new DiffSegment(text, changed)];
}
