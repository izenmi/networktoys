using PingWatcher.Core.Logging;
using Xunit;

namespace PingWatcher.Core.Tests;

public class SyslogParserTests
{
    [Fact]
    public void The_priority_splits_into_facility_and_severity()
    {
        // <34> = facility 4 (auth), severity 2 (crit)
        SyslogMessage m = SyslogParser.Parse("<34>Oct 11 22:14:15 host su: failed");

        Assert.True(m.HasPriority);
        Assert.Equal(4, m.Facility);
        Assert.Equal(2, m.Severity);
        Assert.Equal("crit", m.SeverityName);
        Assert.Contains("su: failed", m.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_without_a_priority_is_kept_whole()
    {
        SyslogMessage m = SyslogParser.Parse("plain message without pri");

        Assert.False(m.HasPriority);
        Assert.Equal("plain message without pri", m.Message);
    }

    [Theory]
    [InlineData("<999>x")]     // PRI が範囲外
    [InlineData("<abc>x")]     // 数字でない
    [InlineData("<>x")]        // 空
    public void A_broken_priority_falls_back_to_whole_text(string line)
    {
        SyslogMessage m = SyslogParser.Parse(line);

        Assert.False(m.HasPriority);
        Assert.Equal(line, m.Message);
    }

    [Fact]
    public void Severity_thresholds_classify_lines()
    {
        Assert.True(SyslogParser.IsSevere(0));    // emerg
        Assert.True(SyslogParser.IsSevere(3));    // err
        Assert.False(SyslogParser.IsSevere(4));
        Assert.True(SyslogParser.IsWarning(4));   // warning
        Assert.False(SyslogParser.IsWarning(6));  // info
    }

    [Fact]
    public void Parsing_never_throws_on_odd_input()
    {
        foreach (string? line in new[] { null, "", "   ", "<", "<0>", "<191>edge" })
        {
            SyslogMessage m = SyslogParser.Parse(line);
            Assert.NotNull(m.Message);
        }
    }
}
