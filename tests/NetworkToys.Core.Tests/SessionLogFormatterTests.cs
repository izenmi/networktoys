using System.Globalization;
using NetworkToys.Core.Logging;
using NetworkToys.Core.Models;
using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

public class SessionLogFormatterTests
{
    private static readonly DateTime At = new(2026, 8, 15, 9, 30, 1, 234, DateTimeKind.Local);

    [Fact]
    public void The_header_names_the_session()
    {
        IReadOnlyList<string> header = SessionLogFormatter.Header(At, 1000, 12, "ICMP");

        Assert.Equal(3, header.Count);
        Assert.Contains("2026/08/15 09:30:01", header[1], StringComparison.Ordinal);
        Assert.Contains("ICMP", header[1], StringComparison.Ordinal);
        Assert.Contains("1000 ms", header[1], StringComparison.Ordinal);
        Assert.Contains("12 件", header[1], StringComparison.Ordinal);

        // 列見出しとサンプル行のタブ数が揃っていること（Excel 貼り付けで列がずれない）
        var ok = ProbeSample.Success(At.Ticks, 1.23);
        Assert.Equal(
            header[2].Count(c => c == '\t'),
            SessionLogFormatter.Sample(At, "host", "192.168.1.1", ok).Count(c => c == '\t'));
    }

    [Fact]
    public void A_success_line_has_time_status_and_rtt()
    {
        var sample = ProbeSample.Success(At.Ticks, 1.26);

        string line = SessionLogFormatter.Sample(At, "web-01", "192.168.1.20", sample);

        Assert.Equal("09:30:01.2\tweb-01\t192.168.1.20\tOK\t1.3", line);
    }

    [Fact]
    public void A_failure_line_has_no_rtt()
    {
        var sample = ProbeSample.Failure(At.Ticks, ProbeStatus.TimedOut);

        string line = SessionLogFormatter.Sample(At, "web-01", "192.168.1.20", sample);

        Assert.EndsWith("\t無応答\t", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_keeps_its_rtt()
    {
        // 拒否は RST が返るまでの実測値を持つ
        var sample = new ProbeSample(At.Ticks, 2.5f, ProbeStatus.Refused);

        string line = SessionLogFormatter.Sample(At, "web-01", "192.168.1.20", sample);

        Assert.Contains("接続拒否\t2.5", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Outage_events_stand_out()
    {
        var open = new OutageRecord("k", "192.168.1.20", At.Ticks, null, 2, 1000, OutageCloseReason.Ongoing, ProbeStatus.TimedOut, false);
        var closed = open with { EndedAtTicks = At.AddSeconds(125).Ticks, CloseReason = OutageCloseReason.Recovered };

        Assert.Contains("*** 不通 192.168.1.20", SessionLogFormatter.OutageOpened(At, open), StringComparison.Ordinal);
        Assert.Contains("*** 復旧 192.168.1.20", SessionLogFormatter.OutageClosed(At, closed), StringComparison.Ordinal);
        Assert.Contains("*** 打ち切り", SessionLogFormatter.OutageClosed(At, closed with { CloseReason = OutageCloseReason.Stopped }), StringComparison.Ordinal);
    }

    [Fact]
    public void Formatting_ignores_the_current_culture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            // カンマ小数点のカルチャでも列が壊れないこと
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var sample = ProbeSample.Success(At.Ticks, 1.26);
            string line = SessionLogFormatter.Sample(At, "h", "a", sample);

            Assert.Contains("1.3", line, StringComparison.Ordinal);
            Assert.DoesNotContain("1,3", line, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
