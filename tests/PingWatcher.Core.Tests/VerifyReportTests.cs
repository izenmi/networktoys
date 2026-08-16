using PingWatcher.Core.Reporting;
using PingWatcher.Core.Verify;
using Xunit;

namespace PingWatcher.Core.Tests;

/// <summary>
/// 業務確認試験の結果を記録に載せる部分。
///
/// <b>試験だけの記録</b>（測定が空）と<b>測定と試験が揃った記録</b>の両方が
/// 成り立つことを固める。試験タブ単独の書き出しでは Rows が空になるので、
/// 空の測定表が出ないことが要。
/// </summary>
public class VerifyReportTests
{
    private static readonly CheckResult[] Results =
    [
        new("インターネットが見られる", CheckKind.Http, "https://www.example.jp/", "Zscaler",
            CheckVerdict.Pass, "HTTP 200", 143),
        new("社内ポータルが開く", CheckKind.Http, "http://portal.example.jp/", "i-FILTER",
            CheckVerdict.Fail, "接続できませんでした", 5000),
        new("Teams が使える", CheckKind.Teams, "", "直接",
            CheckVerdict.Warn, "UDP 3478 ○ / 往復 210 ms", 2100),
        new("勤怠システムにログインできる", CheckKind.Manual, "https://kintai.example.jp/", "",
            CheckVerdict.AwaitingPerson, "ブラウザで開きました。"),
    ];

    private static ReportData ChecksOnly() => new(
        Title: "業務確認試験",
        GeneratedAt: new DateTime(2026, 8, 16, 10, 0, 0, DateTimeKind.Local),
        Note: "",
        StartedAt: null,
        IntervalMs: 0,
        Environment: [("試験した端末", "PC-01")],
        Rows: [],
        Checks: Results);

    // ===== HTML =====

    [Fact]
    public void The_html_carries_every_check()
    {
        string html = HtmlReportWriter.Render(ChecksOnly());

        Assert.Contains("業務確認試験", html, StringComparison.Ordinal);

        foreach (CheckResult result in Results)
            Assert.Contains(result.Name, html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_checks_only_report_has_no_empty_measurement_table()
    {
        // 試験タブから出すと測定は空。そこで「記録された宛先がありません」と
        // 出すと、測ったのに落ちたように読める
        string html = HtmlReportWriter.Render(ChecksOnly());

        Assert.DoesNotContain("記録された宛先がありません", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h2>測定結果</h2>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_report_still_says_so()
    {
        // 試験も測定も無いなら、黙って空にはしない
        var empty = ChecksOnly() with { Checks = null };

        Assert.Contains("記録された宛先がありません", HtmlReportWriter.Render(empty), StringComparison.Ordinal);
    }

    [Fact]
    public void Failures_come_first_in_the_html()
    {
        // 件数が多いとき、不合格を下まで探させない
        string html = HtmlReportWriter.Render(ChecksOnly());

        int failed = html.IndexOf("社内ポータルが開く", StringComparison.Ordinal);
        int passed = html.IndexOf("インターネットが見られる", StringComparison.Ordinal);

        Assert.True(failed < passed, "不合格が合格より後ろにある");
    }

    [Fact]
    public void The_interval_is_left_out_when_there_was_no_measurement()
    {
        // 試験だけの記録に「間隔 0 ms」と書くと誤解させる
        Assert.DoesNotContain("0 ms</span>", HtmlReportWriter.Render(ChecksOnly()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_verdict_keeps_its_symbol_and_words()
    {
        // 色だけで表さない決まりは、白黒で刷られる紙の上でも同じ
        string html = HtmlReportWriter.Render(ChecksOnly());

        Assert.Contains("✕ 不合格", html, StringComparison.Ordinal);
        Assert.Contains("○ 合格", html, StringComparison.Ordinal);
        Assert.Contains("△ 注意", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_html_stays_self_contained()
    {
        string html = HtmlReportWriter.Render(ChecksOnly());

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html.Replace("http://portal.example.jp/", "", StringComparison.Ordinal),
                              StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void What_a_person_writes_cannot_break_out()
    {
        var evil = ChecksOnly() with
        {
            Checks = [new("<script>alert(1)</script>", CheckKind.Http, "", "", CheckVerdict.Pass, "&<>")],
        };

        string html = HtmlReportWriter.Render(evil);

        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    // ===== テキスト =====

    [Fact]
    public void The_text_report_has_its_own_section()
    {
        string text = TextReportWriter.Render(ChecksOnly());

        Assert.Contains("[業務確認試験]", text, StringComparison.Ordinal);
        Assert.Contains("[NG]", text, StringComparison.Ordinal);
        Assert.Contains("社内ポータルが開く", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_verdict_counts_the_failures()
    {
        Assert.Contains("業務確認試験 不合格 1 件", TextReportWriter.Render(ChecksOnly()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_report_skips_the_measurement_table_when_there_is_none()
    {
        Assert.DoesNotContain("[宛先ごとの結果]", TextReportWriter.Render(ChecksOnly()), StringComparison.Ordinal);
    }

    // ===== CSV =====

    [Fact]
    public void The_csv_puts_the_checks_in_a_second_table()
    {
        string csv = CsvReportWriter.Render(ChecksOnly());

        Assert.Contains("[業務確認試験]", csv, StringComparison.Ordinal);
        Assert.Contains("項目,種類,宛先,プロキシ,合否,所要,詳細", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void The_csv_has_no_stray_header_when_nothing_was_measured()
    {
        Assert.DoesNotContain("試行,成功", CsvReportWriter.Render(ChecksOnly()), StringComparison.Ordinal);
    }
}
