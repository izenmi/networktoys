namespace NetworkToys.Core.Work;

/// <summary>文中のひとかたまり。<b>太字・等幅・リンク</b>だけを見分ける。</summary>
/// <param name="Text">そのまま出す文字。</param>
/// <param name="Bold"><c>**…**</c> だったか。</param>
/// <param name="Code">バッククォートで囲まれていたか。</param>
/// <param name="Link"><c>[文字](行き先)</c> の行き先。目次から見出しへ飛ぶのに使う（<c>#見出し</c>）。</param>
public sealed record MarkdownInline(string Text, bool Bold = false, bool Code = false, string? Link = null);

/// <summary>表の 1 マス。</summary>
public sealed record MarkdownCell(IReadOnlyList<MarkdownInline> Text);

/// <summary>文書のかたまり。</summary>
public abstract record MarkdownBlock;

/// <param name="Level">1 が最上位（<c>#</c>）。</param>
public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownInline> Text) : MarkdownBlock;

public sealed record MarkdownParagraph(IReadOnlyList<MarkdownInline> Text) : MarkdownBlock;

/// <param name="Ordered">番号付きか。</param>
public sealed record MarkdownList(bool Ordered, IReadOnlyList<IReadOnlyList<MarkdownInline>> Items)
    : MarkdownBlock;

public sealed record MarkdownTable(
    IReadOnlyList<MarkdownCell> Header,
    IReadOnlyList<IReadOnlyList<MarkdownCell>> Rows) : MarkdownBlock;

/// <summary>コードブロック（``` で囲まれたもの）。中身は解釈しない。</summary>
public sealed record MarkdownCode(string Text) : MarkdownBlock;

/// <summary>引用（<c>&gt; </c> で始まる行）。囲みとして見せる。</summary>
public sealed record MarkdownQuote(IReadOnlyList<MarkdownInline> Text) : MarkdownBlock;

public sealed record MarkdownRule : MarkdownBlock;

/// <summary>
/// Markdown を、画面に組み立てられる形へほどく。
///
/// <b>ライブラリを足さない。</b>読ませたいのは自分たちで書いた 1 本
/// （<c>docs/USAGE.md</c>）だけで、そこに出てくる書き方は
/// 見出し・段落・箇条書き・表・コード・区切り線と、太字・等幅しか無い。
/// xlsx や docx を標準ライブラリで書いているのと同じ判断。
///
/// <b>読めない書き方は、そのままの文字として出す。</b>例外にしない —
/// 使い方が 1 行のために読めなくなる方が困る。
/// </summary>
public static class MarkdownDocument
{
    public static IReadOnlyList<MarkdownBlock> Parse(string? text)
    {
        var blocks = new List<MarkdownBlock>();

        if (string.IsNullOrWhiteSpace(text)) return blocks;

        string[] lines = text.ReplaceLineEndings("\n").Split('\n');

        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;

            // 日本語の文章なので、行の継ぎ目に空白を入れない
            blocks.Add(new MarkdownParagraph(Inlines(string.Concat(paragraph))));
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            string trimmed = line.TrimStart();

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            // コードブロック。閉じが無ければ最後まで
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();

                var code = new List<string>();
                i++;

                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }

                blocks.Add(new MarkdownCode(string.Join('\n', code)));
                continue;
            }

            if (IsRule(trimmed))
            {
                FlushParagraph();
                blocks.Add(new MarkdownRule());
                continue;
            }

            if (HeadingLevel(trimmed) is { } level)
            {
                FlushParagraph();
                blocks.Add(new MarkdownHeading(level, Inlines(trimmed[(level + 1)..].Trim())));
                continue;
            }

            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();

                var rows = new List<string>();

                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    rows.Add(lines[i].Trim());
                    i++;
                }

                i--;   // for の i++ と相殺する

                blocks.Add(Table(rows));
                continue;
            }

            if (QuoteText(trimmed) is { } quote)
            {
                FlushParagraph();

                var quoted = new List<string> { quote };

                while (i + 1 < lines.Length && QuoteText(lines[i + 1].TrimStart()) is { } next)
                {
                    quoted.Add(next);
                    i++;
                }

                blocks.Add(new MarkdownQuote(Inlines(string.Concat(quoted))));
                continue;
            }

            if (BulletText(trimmed) is { } bullet)
            {
                FlushParagraph();

                var items = new List<IReadOnlyList<MarkdownInline>> { Inlines(bullet) };

                while (i + 1 < lines.Length && BulletText(lines[i + 1].TrimStart()) is { } next)
                {
                    items.Add(Inlines(next));
                    i++;
                }

                blocks.Add(new MarkdownList(Ordered: false, items));
                continue;
            }

            if (OrderedText(trimmed) is { } ordered)
            {
                FlushParagraph();

                var items = new List<IReadOnlyList<MarkdownInline>> { Inlines(ordered) };

                while (i + 1 < lines.Length && OrderedText(lines[i + 1].TrimStart()) is { } next)
                {
                    items.Add(Inlines(next));
                    i++;
                }

                blocks.Add(new MarkdownList(Ordered: true, items));
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushParagraph();

        return blocks;
    }

    /// <summary>
    /// 太字（<c>**…**</c>）と等幅（<c>`…`</c>）にほどく。
    /// <b>閉じていない印はただの文字として扱う</b>（消さない）。
    /// </summary>
    public static IReadOnlyList<MarkdownInline> Inlines(string? text)
    {
        var parts = new List<MarkdownInline>();

        if (string.IsNullOrEmpty(text)) return parts;

        var plain = new System.Text.StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0) return;

            parts.Add(new MarkdownInline(plain.ToString()));
            plain.Clear();
        }

        for (int i = 0; i < text.Length;)
        {
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);

                if (end > i)
                {
                    FlushPlain();
                    parts.Add(new MarkdownInline(text[(i + 1)..end], Code: true));
                    i = end + 1;
                    continue;
                }
            }
            else if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);

                if (end > i)
                {
                    FlushPlain();
                    parts.Add(new MarkdownInline(text[(i + 2)..end], Bold: true));
                    i = end + 2;
                    continue;
                }
            }

            else if (text[i] == '[')
            {
                // [文字](行き先)。入れ子は見ない（使い方に出てくるのは目次の 1 段だけ）
                int label = text.IndexOf(']', i + 1);

                if (label > i && label + 1 < text.Length && text[label + 1] == '(')
                {
                    int end = text.IndexOf(')', label + 2);

                    if (end > label)
                    {
                        FlushPlain();
                        parts.Add(new MarkdownInline(
                            text[(i + 1)..label], Link: text[(label + 2)..end]));
                        i = end + 1;
                        continue;
                    }
                }
            }

            plain.Append(text[i]);
            i++;
        }

        FlushPlain();

        return parts;
    }

    /// <summary>
    /// 見出しとリンクの飛び先を突き合わせるための形。
    /// <b>記号と大文字小文字の違いを落とす</b> — GitHub の付け方（小文字にして空白を
    /// <c>-</c> に、記号は落とす）と、手で書いた <c>#見出し</c> のどちらでも同じ形になる。
    /// </summary>
    public static string Anchor(string? text)
    {
        var key = new System.Text.StringBuilder();

        foreach (char c in text ?? "")
        {
            if (char.IsLetterOrDigit(c)) key.Append(char.ToLowerInvariant(c));
        }

        return key.ToString();
    }

    /// <summary>
    /// 目次のリンクのうち、<b>飛び先の見出しが無いもの</b>（書いたとおりの文字で返す）。
    /// 見出しを直したときに目次が黙って死ぬのを防ぐための検査口。
    /// </summary>
    public static IReadOnlyList<string> MissingAnchors(string? text)
    {
        IReadOnlyList<MarkdownBlock> blocks = Parse(text);

        HashSet<string> headings =
            [.. blocks.OfType<MarkdownHeading>().Select(h => Anchor(string.Concat(h.Text.Select(t => t.Text))))];

        return [.. AllInlines(blocks)
            .Where(i => i.Link is { Length: > 1 } link && link[0] == '#')
            .Select(i => i.Link!)
            .Where(link => !headings.Contains(Anchor(link[1..])))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>文書に出てくるひとかたまりを全部並べる（表の中と箇条書きの中も見る）。</summary>
    private static IEnumerable<MarkdownInline> AllInlines(IEnumerable<MarkdownBlock> blocks)
    {
        foreach (MarkdownBlock block in blocks)
        {
            switch (block)
            {
                case MarkdownHeading heading:
                    foreach (MarkdownInline part in heading.Text) yield return part;
                    break;

                case MarkdownParagraph paragraph:
                    foreach (MarkdownInline part in paragraph.Text) yield return part;
                    break;

                case MarkdownQuote quote:
                    foreach (MarkdownInline part in quote.Text) yield return part;
                    break;

                case MarkdownList list:
                    foreach (IReadOnlyList<MarkdownInline> item in list.Items)
                    {
                        foreach (MarkdownInline part in item) yield return part;
                    }

                    break;

                case MarkdownTable table:
                    foreach (MarkdownCell cell in table.Header)
                    {
                        foreach (MarkdownInline part in cell.Text) yield return part;
                    }

                    foreach (IReadOnlyList<MarkdownCell> row in table.Rows)
                    {
                        foreach (MarkdownCell cell in row)
                        {
                            foreach (MarkdownInline part in cell.Text) yield return part;
                        }
                    }

                    break;
            }
        }
    }

    private static MarkdownTable Table(IReadOnlyList<string> rows)
    {
        var cells = new List<IReadOnlyList<MarkdownCell>>();

        foreach (string row in rows)
        {
            // 区切りの行（|---|---|）は見出しの下線。中身ではない
            if (IsTableSeparator(row)) continue;

            cells.Add([.. Split(row).Select(c => new MarkdownCell(Inlines(c)))]);
        }

        return cells.Count == 0
            ? new MarkdownTable([], [])
            : new MarkdownTable(cells[0], [.. cells.Skip(1)]);
    }

    /// <summary>行を <c>|</c> で割る。前後の <c>|</c> は落とす。</summary>
    private static IEnumerable<string> Split(string row)
    {
        string text = row.Trim();

        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|')) text = text[..^1];

        return text.Split('|').Select(c => c.Trim());
    }

    private static bool IsTableSeparator(string row)
        => Split(row).All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '));

    private static bool IsRule(string line)
        => line.Length >= 3 && (line.All(c => c == '-') || line.All(c => c == '*') || line.All(c => c == '_'));

    private static int? HeadingLevel(string line)
    {
        int level = 0;

        while (level < line.Length && line[level] == '#') level++;

        return level is > 0 and <= 6 && level < line.Length && line[level] == ' ' ? level : null;
    }

    /// <summary>引用の行。<c>&gt;</c> だけの行は空文字として続きに繋ぐ。</summary>
    private static string? QuoteText(string line)
    {
        if (!line.StartsWith('>')) return null;

        return line.Length > 1 ? line[1..].Trim() : "";
    }

    private static string? BulletText(string line)
        => (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            ? line[2..].Trim()
            : null;

    private static string? OrderedText(string line)
    {
        int digits = 0;

        while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;

        return digits > 0 && digits + 1 < line.Length && line[digits] == '.' && line[digits + 1] == ' '
            ? line[(digits + 2)..].Trim()
            : null;
    }
}
