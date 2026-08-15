using PingWatcher.Core.Addressing;
using Xunit;

namespace PingWatcher.Core.Tests;

public class ScanRangeParserTests
{
    [Fact]
    public void Empty_input_yields_nothing()
    {
        Assert.Equal(0, ScanRangeParser.Parse(null).Count);
        Assert.Equal(0, ScanRangeParser.Parse("   ").Count);
    }

    [Fact]
    public void Expands_a_cidr()
        => Assert.Equal(254, ScanRangeParser.Parse("192.168.1.0/24").Count);

    [Fact]
    public void Accepts_a_single_address()
    {
        ScanRangeResult result = ScanRangeParser.Parse("192.168.1.10");

        Assert.Equal(1, result.Count);
        Assert.False(result.HasErrors);
    }

    [Theory]
    [InlineData("192.168.1.1-4, 192.168.2.1-4")]
    [InlineData("192.168.1.1-4\n192.168.2.1-4")]
    [InlineData("192.168.1.1-4 192.168.2.1-4")]
    public void Separators_can_be_comma_newline_or_space(string text)
        => Assert.Equal(8, ScanRangeParser.Parse(text).Count);

    [Fact]
    public void Duplicates_are_removed()
    {
        // 範囲が重なっても二重にスキャンしない
        ScanRangeResult result = ScanRangeParser.Parse("192.168.1.1-10\n192.168.1.5-15");

        Assert.Equal(15, result.Count);
    }

    [Fact]
    public void Keeps_the_order_of_first_appearance()
    {
        ScanRangeResult result = ScanRangeParser.Parse("10.0.0.3\n10.0.0.1\n10.0.0.2");

        Assert.Equal("10.0.0.3", result.Addresses[0].ToString());
        Assert.Equal("10.0.0.1", result.Addresses[1].ToString());
    }

    [Fact]
    public void Comment_lines_are_ignored()
    {
        ScanRangeResult result = ScanRangeParser.Parse("# 事務所\n192.168.1.1-4\n; 予備\n　全角も");

        Assert.Equal(4, result.Count);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Hostnames_are_rejected_with_a_reason()
    {
        ScanRangeResult result = ScanRangeParser.Parse("example.jp");

        Assert.Equal(0, result.Count);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Stops_at_the_limit()
    {
        ScanRangeResult result = ScanRangeParser.Parse("10.0.0.0/16", limit: 4096);

        Assert.True(result.HasErrors);
        Assert.True(result.Count <= 4096);
    }

    [Fact]
    public void Partial_errors_do_not_discard_the_valid_part()
    {
        ScanRangeResult result = ScanRangeParser.Parse("192.168.1.1-4\nおかしな行\n192.168.1.20");

        Assert.Equal(5, result.Count);
        Assert.True(result.HasErrors);
    }
}
