using NetworkToys.Core.Design;
using Xunit;

namespace NetworkToys.Core.Tests;

public class ColorMathTests
{
    [Fact]
    public void Black_on_white_is_the_maximum()
    {
        double? ratio = ColorMath.ContrastRatio("#000000", "#FFFFFF");

        Assert.NotNull(ratio);
        Assert.Equal(21.0, ratio!.Value, precision: 2);
    }

    [Fact]
    public void The_same_colour_has_no_contrast()
    {
        Assert.Equal(1.0, ColorMath.ContrastRatio("#3D3650", "#3D3650")!.Value, precision: 6);
    }

    [Fact]
    public void The_order_does_not_matter()
    {
        double? a = ColorMath.ContrastRatio("#6FE7B4", "#171E33");
        double? b = ColorMath.ContrastRatio("#171E33", "#6FE7B4");

        Assert.Equal(a!.Value, b!.Value, precision: 9);
    }

    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("#ffffff")]
    [InlineData("ffffff")]
    [InlineData("#FFFFFFFF")]
    public void Hex_is_read_in_the_shapes_that_appear_in_the_palette(string text)
    {
        Assert.True(ColorMath.TryParseHex(text, out byte r, out byte g, out byte b));
        Assert.Equal(255, r);
        Assert.Equal(255, g);
        Assert.Equal(255, b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("みどり")]
    public void Anything_else_is_refused(string? text)
    {
        Assert.False(ColorMath.TryParseHex(text, out _, out _, out _));
        Assert.Null(ColorMath.ContrastRatio(text, "#FFFFFF"));
    }

    [Fact]
    public void Green_weighs_more_than_blue()
    {
        // 同じ強さでも緑のほうが明るく見える。ここが平均でないことの確認
        double green = ColorMath.RelativeLuminance(0, 255, 0);
        double blue = ColorMath.RelativeLuminance(0, 0, 255);

        Assert.True(green > blue * 5, $"緑 {green} が青 {blue} に対して重くない");
    }

    [Fact]
    public void The_light_body_text_clears_the_threshold()
    {
        // 実際に使う組み合わせ。ここが割れたらパレットを直す
        double? ratio = ColorMath.ContrastRatio("#3D3650", "#FFFFFF");

        Assert.True(ratio >= ColorMath.MinimumForText, $"本文のコントラストが不足: {ratio}");
    }

    [Fact]
    public void The_dark_body_text_clears_the_threshold()
    {
        double? ratio = ColorMath.ContrastRatio("#E8ECF8", "#171E33");

        Assert.True(ratio >= ColorMath.MinimumForText, $"本文のコントラストが不足: {ratio}");
    }

    [Theory]
    [InlineData("# F0012")]
    [InlineData("#FF 012")]
    [InlineData("#\tFF0012")]
    public void Hex_with_embedded_whitespace_is_rejected(string text)
    {
        // NumberStyles.HexNumber は空白を許す。打ち間違いを「読めたから正しい」と
        // 信じる自己診断の前提が崩れるので、桁は厳密に見る
        Assert.False(ColorMath.TryParseHex(text, out _, out _, out _));
    }

    [Fact]
    public void The_alpha_prefix_is_dropped_not_the_suffix()
    {
        // WPF の #AARRGGBB 準拠。#RRGGBBAA 実装と取り違えると
        // 半透明色のコントラスト検算が別の色で行われてしまう
        Assert.True(ColorMath.TryParseHex("#80FF0012", out byte r, out byte g, out byte b));

        Assert.Equal(0xFF, r);
        Assert.Equal(0x00, g);
        Assert.Equal(0x12, b);
    }
}
