using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// 使い方（docs/USAGE.md）を画面で組むための読み取り。
/// <b>読めない書き方は文字として出す</b>（1 行のために全体が読めなくなる方が困る）。
/// </summary>
public class MarkdownDocumentTests
{
    [Fact]
    public void Headings_paragraphs_lists_and_rules_are_separated()
    {
        const string text = """
            # 使い方

            最初の段落です。
            続きの行。

            ## 次の節

            - ひとつ
            - ふたつ

            1. 手順 1
            2. 手順 2

            ---
            """;

        IReadOnlyList<MarkdownBlock> blocks = MarkdownDocument.Parse(text);

        Assert.Equal(1, blocks.OfType<MarkdownHeading>().First().Level);
        Assert.Equal(2, blocks.OfType<MarkdownHeading>().Last().Level);

        // 日本語なので、行の継ぎ目に空白を入れない
        Assert.Equal("最初の段落です。続きの行。", Text(blocks.OfType<MarkdownParagraph>().First().Text));

        MarkdownList[] lists = [.. blocks.OfType<MarkdownList>()];

        Assert.Equal(2, lists.Length);
        Assert.False(lists[0].Ordered);
        Assert.Equal(2, lists[0].Items.Count);
        Assert.True(lists[1].Ordered);
        Assert.Equal("手順 2", Text(lists[1].Items[1]));

        Assert.Single(blocks.OfType<MarkdownRule>());
    }

    [Fact]
    public void Tables_keep_their_header_and_rows()
    {
        const string text = """
            | タブ | 内容 |
            |---|---|
            | Ping | 並列 ping |
            | Wi-Fi | 電波の状態 |
            """;

        MarkdownTable table = MarkdownDocument.Parse(text).OfType<MarkdownTable>().Single();

        Assert.Equal(2, table.Header.Count);
        Assert.Equal("タブ", Text(table.Header[0].Text));

        // 区切りの行（|---|）は中身ではない
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Wi-Fi", Text(table.Rows[1][0].Text));
    }

    [Fact]
    public void Bold_and_code_are_marked_up()
    {
        IReadOnlyList<MarkdownInline> parts =
            MarkdownDocument.Inlines("**大事**なのは `show run` です");

        Assert.True(parts[0].Bold);
        Assert.Equal("大事", parts[0].Text);
        Assert.True(parts.Single(p => p.Code).Text == "show run");

        // 閉じていない印は、ただの文字として残す（消さない）
        Assert.Equal("**閉じない", Text(MarkdownDocument.Inlines("**閉じない")));
    }

    [Fact]
    public void Code_blocks_are_kept_as_they_are()
    {
        const string text = """
            ```
            copy running-config ftp:
            ```
            """;

        Assert.Equal("copy running-config ftp:", MarkdownDocument.Parse(text).OfType<MarkdownCode>().Single().Text);
    }

    [Fact]
    public void Nothing_readable_is_not_an_error()
    {
        Assert.Empty(MarkdownDocument.Parse(null));
        Assert.Empty(MarkdownDocument.Parse("   "));
    }

    private static string Text(IEnumerable<MarkdownInline> parts)
        => string.Concat(parts.Select(p => p.Text));
}
