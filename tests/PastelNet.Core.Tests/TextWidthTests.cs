using PastelNet.Core.Reporting;
using Xunit;

namespace PastelNet.Core.Tests;

public class TextWidthTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("192.168.1.1", 11)]
    [InlineData("あいう", 6)]
    [InlineData("3階", 3)]
    [InlineData("ＡＢＣ", 6)]
    public void Full_width_characters_count_as_two(string text, int expected)
    {
        Assert.Equal(expected, TextWidth.Of(text));
    }

    [Fact]
    public void Padding_lines_up_mixed_text()
    {
        // ここが本題。PadRight では日本語の入った列がずれる
        string a = TextWidth.Pad("あいう", 10);
        string b = TextWidth.Pad("abcdef", 10);

        Assert.Equal(10, TextWidth.Of(a));
        Assert.Equal(10, TextWidth.Of(b));
    }

    [Fact]
    public void Padding_leaves_text_that_is_already_too_wide()
    {
        Assert.Equal("あいうえお", TextWidth.Pad("あいうえお", 4));
    }

    [Fact]
    public void Numbers_can_be_pushed_to_the_right()
    {
        Assert.Equal("   1.40", TextWidth.PadLeft("1.40", 7));
    }

    [Fact]
    public void Truncating_marks_that_it_was_cut()
    {
        string text = TextWidth.Truncate("1階 EPS ラック上段", 10);

        Assert.EndsWith("…", text, StringComparison.Ordinal);
        Assert.True(TextWidth.Of(text) <= 10, $"幅が超えている: {TextWidth.Of(text)}");
    }

    [Fact]
    public void Truncating_never_splits_a_full_width_character()
    {
        // 全角の途中で切ると化ける。幅で数えながら詰めていることの確認
        string text = TextWidth.Truncate("あいうえお", 6);

        Assert.True(TextWidth.Of(text) <= 6);
        Assert.DoesNotContain('�', text);
    }

    [Fact]
    public void Short_text_is_left_alone_by_truncation()
    {
        Assert.Equal("abc", TextWidth.Truncate("abc", 10));
    }

    [Fact]
    public void A_cell_always_has_the_asked_for_width()
    {
        foreach (string source in new[] { "", "a", "あいうえおかきくけこ", "192.168.100.200" })
            Assert.Equal(12, TextWidth.Of(TextWidth.Cell(source, 12)));
    }
}
