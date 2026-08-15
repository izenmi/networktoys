using PingWatcher.Core.Metrics;
using PingWatcher.Core.Models;
using PingWatcher.Core.Reporting;
using PingWatcher.Core.Work;
using Xunit;

namespace PingWatcher.Core.Tests;

public class TextReportWriterTests
{
    private static readonly DateTime Started = new(2026, 8, 15, 9, 0, 0, DateTimeKind.Local);

    private static ReportRow Row(
        string host,
        string comment = "",
        int attempts = 100,
        int successes = 100,
        string state = "応答",
        bool isDown = false)
        => new(
            host,
            "192.168.1.1",
            comment,
            "ICMP",
            new RttStatistics(attempts, successes, attempts == 0 ? 0 : 100.0 * (attempts - successes) / attempts,
                              1.0, 1.4, 8.0, 2.0, 0.3),
            [1, 2, 1],
            state,
            isDown);

    private static ReportData Data(
        IReadOnlyList<ReportRow>? rows = null,
        string? ipConfig = null,
        IReadOnlyList<(string, string)>? wireless = null,
        IReadOnlyList<OutageRecord>? outages = null,
        string? wirelessNote = null)
        => new(
            Title: "疎通確認",
            GeneratedAt: new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Local),
            Note: "定期点検",
            StartedAt: Started,
            IntervalMs: 1000,
            Environment: [("IP", "192.168.1.20/24")],
            Rows: rows ?? [Row("gw-01")],
            IpConfig: ipConfig,
            Work: null,
            Wireless: wireless,
            Outages: outages,
            WirelessNote: wirelessNote);

    [Fact]
    public void The_title_and_the_times_are_at_the_top()
    {
        string text = TextReportWriter.Render(Data());

        Assert.Contains("疎通確認", text, StringComparison.Ordinal);
        Assert.Contains("2026/08/15 09:30:00", text, StringComparison.Ordinal);
        Assert.Contains("1000 ms 間隔", text, StringComparison.Ordinal);
        Assert.Contains("定期点検", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_verdict_comes_before_the_table()
    {
        // 長い表を読ませる前に結論を置く
        string text = TextReportWriter.Render(Data());

        Assert.True(text.IndexOf("[判定]", StringComparison.Ordinal)
                    < text.IndexOf("[宛先ごとの結果]", StringComparison.Ordinal));
    }

    [Fact]
    public void A_target_with_no_answer_is_called_out()
    {
        ReportRow down = Row("sw-03", "3階 島HUB", attempts: 120, successes: 0, state: "不達", isDown: true);
        string text = TextReportWriter.Render(Data([Row("gw-01"), down]));

        Assert.Contains("★ 応答なし 1 件", text, StringComparison.Ordinal);
        Assert.Contains("[応答に問題があった宛先]", text, StringComparison.Ordinal);
        Assert.Contains("[NG]", text, StringComparison.Ordinal);
        Assert.Contains("sw-03", text, StringComparison.Ordinal);
        Assert.Contains("3階 島HUB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_target_that_recovered_is_separated_from_one_still_down()
    {
        // 「いま落ちている」と「途中で落ちた」は手当てが違う
        ReportRow lossy = Row("ap-2f", attempts: 100, successes: 91);
        string text = TextReportWriter.Render(Data([lossy]));

        Assert.Contains("応答なし 0 件", text, StringComparison.Ordinal);
        Assert.Contains("途中で応答が途切れた宛先 1 件", text, StringComparison.Ordinal);
        Assert.Contains("[!!]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[NG]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_run_says_so_without_a_problem_section()
    {
        string text = TextReportWriter.Render(Data());

        Assert.Contains("応答なし 0 件", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[応答に問題があった宛先]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_outage_log_carries_the_times()
    {
        var outage = new OutageRecord(
            "gw-01|Icmp|0",
            "gw-01",
            Started.AddMinutes(3).Ticks,
            Started.AddMinutes(3).AddSeconds(8).Ticks,
            MissedProbes: 8,
            IntervalMs: 1000,
            CloseReason: OutageCloseReason.Recovered,
            DominantStatus: ProbeStatus.TimedOut,
            StartUnknown: false);

        string text = TextReportWriter.Render(Data(outages: [outage]));

        Assert.Contains("[不通の記録]", text, StringComparison.Ordinal);
        Assert.Contains("09:03:00", text, StringComparison.Ordinal);
        Assert.Contains("09:03:08", text, StringComparison.Ordinal);
        Assert.Contains("無応答", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ongoing_outage_is_not_given_an_end_time()
    {
        var outage = new OutageRecord(
            "sw-03|Icmp|0", "sw-03", Started.Ticks, null, 30, 1000,
            OutageCloseReason.Recovered, ProbeStatus.TimedOut, StartUnknown: false);

        string text = TextReportWriter.Render(Data(outages: [outage]));

        Assert.Contains("継続中", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ipconfig_output_is_carried_verbatim()
    {
        const string ipConfig = "Windows IP 構成\n\n   ホスト名. . . . . . . . . . . . . : PC-01";
        string text = TextReportWriter.Render(Data(ipConfig: ipConfig));

        Assert.Contains("[ipconfig /all]", text, StringComparison.Ordinal);
        Assert.Contains("ホスト名. . . . . . . . . . . . . : PC-01", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wireless_details_are_written()
    {
        string text = TextReportWriter.Render(Data(wireless: [("SSID", "office-5g"), ("信号", "-52 dBm")]));

        Assert.Contains("[無線 LAN]", text, StringComparison.Ordinal);
        Assert.Contains("office-5g", text, StringComparison.Ordinal);
        Assert.Contains("-52 dBm", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_wireless_says_why_rather_than_going_blank()
    {
        string text = TextReportWriter.Render(Data(wirelessNote: "無線 LAN の情報は取得していません。"));

        Assert.Contains("[無線 LAN]", text, StringComparison.Ordinal);
        Assert.Contains("取得していません", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_state_marks_stay_the_same_width()
    {
        // 表が崩れないことが記号を ASCII にしている理由
        string[] marks = ["[NG]", "[!!]", "[OK]", "[--]"];

        Assert.All(marks, m => Assert.Equal(4, TextWidth.Of(m)));
    }

    [Fact]
    public void Japanese_comments_do_not_break_the_columns()
    {
        // 全角は 2 桁ぶん取る。文字数で揃えると日本語の備考で表がずれる
        string text = TextReportWriter.Render(Data([Row("a", "あいう"), Row("bbbbbb", "abc")]));

        string[] lines = [.. text.Split('\n').Where(l => l.Contains("ICMP", StringComparison.Ordinal))];

        Assert.Equal(2, lines.Length);

        // 備考の直前までは同じ幅に揃っている
        int first = lines[0].IndexOf("ICMP", StringComparison.Ordinal);
        int second = lines[1].IndexOf("ICMP", StringComparison.Ordinal);
        Assert.Equal(first, second);
    }
}
