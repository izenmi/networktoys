namespace PingWatcher.Core.Design;

/// <summary>
/// 配色の読みやすさを数値で確かめるための計算。
///
/// 「パステルだから淡くしたい」と「読めなければ意味がない」は必ず衝突する。
/// 目で見て決めると、明るい画面で作った色が暗い画面で沈む事故が起きるので、
/// ここで機械的に判定できるようにしておく。WCAG 2.x のコントラスト比の式。
/// </summary>
public static class ColorMath
{
    /// <summary>本文として読ませる文字に求める比。WCAG の AA 基準。</summary>
    public const double MinimumForText = 4.5;

    /// <summary>
    /// 相対輝度。単なる明るさの平均ではなく、人の目の感度に合わせて
    /// 緑を重く、青を軽く見積もる。
    /// </summary>
    public static double RelativeLuminance(byte r, byte g, byte b)
        => (0.2126 * Linearize(r)) + (0.7152 * Linearize(g)) + (0.0722 * Linearize(b));

    /// <summary>2 色のコントラスト比。1.0（同色）〜 21.0（白と黒）。</summary>
    public static double ContrastRatio(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        double a = RelativeLuminance(r1, g1, b1);
        double c = RelativeLuminance(r2, g2, b2);

        (double lighter, double darker) = a >= c ? (a, c) : (c, a);

        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary><c>#RRGGBB</c> または <c>#AARRGGBB</c> を解釈する。</summary>
    public static bool TryParseHex(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;

        if (string.IsNullOrWhiteSpace(text)) return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#') span = span[1..];

        // アルファは読み飛ばす。地色の上に不透明で置く前提で比べる
        if (span.Length == 8) span = span[2..];
        if (span.Length != 6) return false;

        // NumberStyles.HexNumber は前後の空白を許してしまう。パレットの打ち間違いを
        // 自己診断が「読めたから正しい」と信じる作りなので、桁は厳密に見る
        foreach (char c in span)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }

        return TryHexByte(span[..2], out r)
               && TryHexByte(span.Slice(2, 2), out g)
               && TryHexByte(span.Slice(4, 2), out b);
    }

    /// <summary>2 色のコントラスト比を <c>#RRGGBB</c> 表記から求める。解釈できなければ null。</summary>
    public static double? ContrastRatio(string? foreground, string? background)
    {
        if (!TryParseHex(foreground, out byte fr, out byte fg, out byte fb)) return null;
        if (!TryParseHex(background, out byte br, out byte bg, out byte bb)) return null;

        return ContrastRatio(fr, fg, fb, br, bg, bb);
    }

    private static bool TryHexByte(ReadOnlySpan<char> span, out byte value)
        => byte.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out value);

    private static double Linearize(byte channel)
    {
        double v = channel / 255.0;

        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
