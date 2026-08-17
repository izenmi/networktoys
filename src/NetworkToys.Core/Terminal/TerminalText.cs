using System.Text;

namespace NetworkToys.Core.Terminal;

/// <summary>
/// 端末から返ってきた生の文字列を、保存して読める形に均す。
///
/// <b>順序が要。</b>バックスペースを先に適用しないと、ページャの消去列
/// （<c>--More--</c> をバックスペースで消しにくる）が残骸として残る。
/// 素朴な文字列置換だけで済ませると空白の帯が残って読みにくくなる。
/// </summary>
public static class TerminalText
{
    /// <summary>ANSI エスケープの開始（0x1B）。ソースに生の制御文字を置かない。</summary>
    private const char Escape = '\u001b';

    /// <summary>ベル（0x07）。端末を鳴らすだけなので保存しない。</summary>
    private const char Bell = '\u0007';

    /// <summary>生の受信文字列を保存用に均す。</summary>
    public static string Clean(string raw)
    {
        if (raw.Length == 0) return "";

        // --More-- はここでは落とさない。ページャが出ていることを
        // 呼び出し側が判定してから、出力を組み立てるときに落とす
        // (先に消すと EndsWithMore が永久に false になり、ページャで止まる)
        string text = ApplyBackspaces(raw);
        text = StripAnsi(text);
        return NormalizeNewlines(text);
    }

    /// <summary>
    /// バックスペースを実際に適用する。<b>行頭を越えて食わない</b>
    /// （前の行の文字まで消すと出力が壊れる）。
    /// </summary>
    internal static string ApplyBackspaces(string text)
    {
        if (!text.Contains('\b', StringComparison.Ordinal)) return text;

        var builder = new StringBuilder(text.Length);
        int lineStart = 0;

        foreach (char c in text)
        {
            if (c == '\b')
            {
                if (builder.Length > lineStart)
                    builder.Length--;

                continue;
            }

            builder.Append(c);

            if (c is '\n' or '\r')
                lineStart = builder.Length;
        }

        return builder.ToString();
    }

    /// <summary>
    /// ANSI エスケープを落とす。CSI（<c>ESC [ … 終端</c>）と
    /// OSC（<c>ESC ] … BEL または ESC \</c>）、および単独の ESC。
    /// </summary>
    internal static string StripAnsi(string text)
    {
        if (!text.Contains(Escape, StringComparison.Ordinal)) return text;

        var builder = new StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != Escape)
            {
                builder.Append(text[i]);
                continue;
            }

            if (i + 1 >= text.Length) break;   // 末尾で切れた ESC は捨てる

            char kind = text[i + 1];
            i++;

            if (kind == '[')
            {
                // CSI: 引数を読み飛ばし、@〜~ の終端で閉じる
                i++;
                while (i < text.Length && text[i] is >= ' ' and < '@') i++;
                // 終端 1 文字を食う(見つからなければ末尾で止まる)
            }
            else if (kind == ']')
            {
                // OSC: BEL か ESC \ まで
                i++;
                while (i < text.Length && text[i] != Bell)
                {
                    if (text[i] == Escape && i + 1 < text.Length && text[i + 1] == '\\')
                    {
                        i++;
                        break;
                    }

                    i++;
                }
            }

            // ESC + 1 文字だけのものは、上のどれでもなければここで捨て終わっている
        }

        return builder.ToString();
    }

    /// <summary>バックスペースで消されなかった <c>--More--</c> を掃除する。</summary>
    public static string StripMorePrompts(string text)
    {
        if (!text.Contains("--More--", StringComparison.Ordinal)
            && !text.Contains("<--- More --->", StringComparison.Ordinal))
            return text;

        return text
            .Replace("<--- More --->", "", StringComparison.Ordinal)
            .Replace("--More--", "", StringComparison.Ordinal);
    }

    /// <summary>改行を LF に揃え、意味のない制御文字を落とす。</summary>
    internal static string NormalizeNewlines(string text)
    {
        var builder = new StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '\r')
            {
                // CRLF は 1 つの改行に。裸の CR も改行として扱う
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                builder.Append('\n');
                continue;
            }

            // NUL と BEL は端末の都合。保存しても読めないだけ
            if (c is '\0' or Bell) continue;

            builder.Append(c);
        }

        return builder.ToString();
    }
}
