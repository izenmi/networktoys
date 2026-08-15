using System.Text;

namespace PingWatcher.Core.Reporting;

/// <summary>
/// 等幅で読ませるテキストの桁揃え。
///
/// <see cref="string.PadRight(int)"/> は文字数しか数えないため、備考に日本語が
/// 入った瞬間に表が崩れる。日本語は 1 文字で 2 桁ぶんの幅を取るので、
/// 見た目の幅で数え直す必要がある。
/// </summary>
public static class TextWidth
{
    /// <summary>見た目の幅。全角は 2、半角は 1 として数える。</summary>
    public static int Of(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int width = 0;
        foreach (Rune rune in text.EnumerateRunes())
            width += WidthOf(rune);

        return width;
    }

    /// <summary>
    /// 右を空白で埋めて幅を揃える。すでに超えていればそのまま返す
    /// （切り詰めたい場合は先に <see cref="Truncate"/> を通すこと）。
    /// </summary>
    public static string Pad(string? text, int width)
    {
        text ??= string.Empty;
        int actual = Of(text);

        return actual >= width ? text : text + new string(' ', width - actual);
    }

    /// <summary>左を空白で埋めて幅を揃える。数値を右寄せするのに使う。</summary>
    public static string PadLeft(string? text, int width)
    {
        text ??= string.Empty;
        int actual = Of(text);

        return actual >= width ? text : new string(' ', width - actual) + text;
    }

    /// <summary>
    /// 幅に収まるところまで切り詰める。切ったことが分かるよう末尾に .. を置く。
    /// 全角の途中で切れないよう、幅で数えながら詰める。
    ///
    /// 省略記号を「…」にしないのは、あの文字が East Asian Ambiguous で、
    /// 日本語等幅フォントでは 2 桁に描かれるため。1 桁と数えて置くと
    /// 切り詰めが起きた行だけ表が 1 桁ずれる（このクラスの存在理由が崩れる）。
    /// 文字単位ではなく Rune 単位で詰めるのは、絵文字などのサロゲートペアを
    /// 途中で割ると壊れた文字で終わる不正な文字列になるため。
    /// </summary>
    public static string Truncate(string? text, int width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0) return string.Empty;
        if (Of(text) <= width) return text;

        const string ellipsis = "..";

        // 省略記号ぶんの幅を空けておく
        int budget = width - ellipsis.Length;
        var builder = new StringBuilder();
        int used = 0;

        foreach (Rune rune in text.EnumerateRunes())
        {
            int w = WidthOf(rune);
            if (used + w > budget) break;

            builder.Append(rune);
            used += w;
        }

        return builder.Append(ellipsis).ToString();
    }

    /// <summary>幅に収めてから右を埋める。表の 1 マスを作るのに使う。</summary>
    public static string Cell(string? text, int width)
        => Pad(Truncate(text, width), width);

    /// <summary>
    /// 全角として扱う文字か。
    ///
    /// East Asian Width の Wide と Fullwidth に当たる範囲を拾っている。
    /// Ambiguous（●▲✕ など）は環境によって幅が変わるため<b>含めない</b>。
    /// 表に使う記号は ASCII に寄せてあるので、ここで悩まなくて済むようにしている。
    /// </summary>
    private static int WidthOf(Rune rune)
        // BMP の外（絵文字・CJK 拡張 B 以降）はほぼすべて全角相当
        => rune.Value > 0xFFFF ? 2 : WidthOf((char)rune.Value);

    private static int WidthOf(char c) => c switch
    {
        >= 'ᄀ' and <= 'ᅟ' => 2,   // ハングル字母
        >= '⺀' and <= '〾' => 2,   // CJK 部首・かな補助・記号
        >= 'ぁ' and <= '㏿' => 2,   // ひらがな・カタカナ・互換
        >= '㐀' and <= '䶿' => 2,   // CJK 拡張A
        >= '一' and <= '鿿' => 2,   // CJK 統合漢字
        >= 'ꀀ' and <= '꓏' => 2,   // イ文字
        >= '가' and <= '힣' => 2,   // ハングル音節
        >= '豈' and <= '﫿' => 2,   // CJK 互換漢字
        >= '︰' and <= '﹏' => 2,   // CJK 互換形
        >= '＀' and <= '｠' => 2,   // 全角英数・記号
        >= '￠' and <= '￦' => 2,   // 全角通貨記号
        _ => 1,
    };
}
