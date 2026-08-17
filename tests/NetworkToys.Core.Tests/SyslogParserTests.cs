using NetworkToys.Core.Logging;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// syslog の重大度は、いちど「[warning] 本文」という 1 本の文字列に潰して
/// 捨てていた（受け取り側に入れ物が無かった）。数値のまま届くことを固定する。
/// </summary>
public class SyslogParserTests
{
    [Theory]
    // PRI = facility * 8 + severity
    [InlineData("<0>emergency", 0, 0, "emerg")]
    [InlineData("<27>%LINK-3-UPDOWN: down", 3, 3, "err")]
    [InlineData("<188>warn text", 23, 4, "warning")]
    [InlineData("<190>info text", 23, 6, "info")]
    public void Priority_is_split_into_facility_and_severity(
        string line, int facility, int severity, string name)
    {
        SyslogMessage message = SyslogParser.Parse(line);

        Assert.True(message.HasPriority);
        Assert.Equal(facility, message.Facility);
        Assert.Equal(severity, message.Severity);
        Assert.Equal(name, message.SeverityName);
    }

    [Fact]
    public void The_body_keeps_no_trace_of_the_priority()
    {
        // 画面には重大度を別の列で出すので、本文に混ぜ込んではいけない
        Assert.Equal("%LINK-3-UPDOWN: down", SyslogParser.Parse("<27>%LINK-3-UPDOWN: down").Message);
    }

    [Theory]
    [InlineData("no priority here")]
    [InlineData("<999>out of range")]
    [InlineData("<abc>not a number")]
    public void A_line_without_a_usable_priority_is_kept_whole(string line)
    {
        SyslogMessage message = SyslogParser.Parse(line);

        Assert.False(message.HasPriority);
        Assert.Equal(line, message.Message);
    }

    [Theory]
    [InlineData(0, true, false)]    // emerg
    [InlineData(3, true, false)]    // err
    [InlineData(4, false, true)]    // warning
    [InlineData(5, false, false)]   // notice
    public void Severity_maps_to_how_loudly_to_show_it(int severity, bool severe, bool warning)
    {
        Assert.Equal(severe, SyslogParser.IsSevere(severity));
        Assert.Equal(warning, SyslogParser.IsWarning(severity));
    }

    [Theory]
    [InlineData(0, "emerg")]
    [InlineData(7, "debug")]
    // 範囲外は空文字。重大度を持たない画面（FTP など）は -1 を渡してくる
    [InlineData(-1, "")]
    [InlineData(8, "")]
    public void Names_outside_the_range_come_back_empty(int severity, string expected)
        => Assert.Equal(expected, SyslogParser.NameOf(severity));
}
