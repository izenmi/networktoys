using PastelNet.Core.Design;
using Xunit;

namespace PastelNet.Core.Tests;

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
}
