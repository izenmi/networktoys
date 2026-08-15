using PingWatcher.Core.Metrics;
using PingWatcher.Core.Reporting;
using Xunit;

namespace PingWatcher.Core.Tests;

public class ReportWriterTests
{
    private static ReportData Sample(string comment = "1階 EPS", string? ipConfig = null) => new(
        Title: "疎通確認",
        GeneratedAt: new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Local),
        Note: "定期点検",
        StartedAt: new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Local),
        IntervalMs: 1000,
        Environment: [("IP", "192.168.1.20/24"), ("ゲートウェイ", "192.168.1.1")],
        Rows:
        [
            new ReportRow(
                "192.168.1.1",
                "192.168.1.1",
                comment,
                "ICMP",
                new RttStatistics(100, 98, 2, 1.0, 1.4, 8.0, 2.0, 0.3),
                [1, 2, 1, 3, 1]),
        ],
        IpConfig: ipConfig);

    [Fact]
    public void Html_is_a_complete_document()
    {
        string html = HtmlReportWriter.Render(Sample());

        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.EndsWith("</html>\n", html, StringComparison.Ordinal);
        Assert.Contains("<title>疎通確認</title>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_carries_no_external_references()
    {
        // 客先のオフライン環境で開ける必要がある。
        // 「何も読みに行かない」ことを、読み込みを起こす書き方の不在で確かめる。
        // SVG の xmlns は URL の形をしているが参照ではないので、対象にしない。
        string html = HtmlReportWriter.Render(Sample());

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_only_url_shaped_text_is_the_svg_namespace()
    {
        string html = HtmlReportWriter.Render(Sample());

        int occurrences = 0;
        for (int i = html.IndexOf("http", StringComparison.Ordinal); i >= 0;
             i = html.IndexOf("http", i + 1, StringComparison.Ordinal))
        {
            occurrences++;
            Assert.StartsWith("http://www.w3.org/2000/svg", html[i..], StringComparison.Ordinal);
        }

        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Html_includes_the_measured_values()
    {
        string html = HtmlReportWriter.Render(Sample());

        Assert.Contains("192.168.1.1", html, StringComparison.Ordinal);
        Assert.Contains("1階 EPS", html, StringComparison.Ordinal);

        // 平均 RTT。MOS と p95 は載せなくなったので、実測値そのものを見る
        Assert.Contains("1.4", html, StringComparison.Ordinal);
        Assert.Contains("<svg", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_escapes_whatever_the_user_typed()
    {
        string html = HtmlReportWriter.Render(Sample(comment: "<script>alert('x')</script>"));

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("普通", "普通")]
    [InlineData("a<b", "a&lt;b")]
    [InlineData("a&b", "a&amp;b")]
    [InlineData("\"quoted\"", "&quot;quoted&quot;")]
    public void Escape_handles_the_dangerous_characters(string input, string expected)
        => Assert.Equal(expected, HtmlReportWriter.Escape(input));

    [Fact]
    public void Csv_uses_crlf_and_has_a_header()
    {
        string csv = CsvReportWriter.Render(Sample());

        Assert.Contains("\r\n", csv, StringComparison.Ordinal);
        Assert.StartsWith("宛先,IP,種別,備考", csv, StringComparison.Ordinal);
        Assert.Equal(2, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Theory]
    [InlineData("普通", "普通")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    public void Csv_quotes_only_when_needed(string input, string expected)
        => Assert.Equal(expected, CsvReportWriter.Quote(input));

    [Fact]
    public void Ipconfig_output_is_included_verbatim()
    {
        const string output = "Windows IP 構成\n\n   ホスト名. . . . . . . . . . . . .: PC-01\n";
        string html = HtmlReportWriter.Render(Sample(ipConfig: output));

        Assert.Contains("ipconfig /all", html, StringComparison.Ordinal);
        Assert.Contains("<pre class=\"console\">", html, StringComparison.Ordinal);
        Assert.Contains("PC-01", html, StringComparison.Ordinal);
        Assert.Contains("ホスト名", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ipconfig_section_is_omitted_when_absent()
    {
        string html = HtmlReportWriter.Render(Sample());

        Assert.DoesNotContain("<pre", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Ipconfig_output_is_escaped_too()
    {
        string html = HtmlReportWriter.Render(Sample(ipConfig: "<b>not markup</b>"));

        Assert.DoesNotContain("<b>not markup</b>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sparkline_is_empty_without_data()
        => Assert.Equal(string.Empty, SvgSparkline.Render([]));

    [Fact]
    public void Sparkline_plots_every_point()
    {
        string svg = SvgSparkline.Render([1, 2, 3, 4]);

        Assert.Contains("<polyline", svg, StringComparison.Ordinal);
        Assert.Equal(4, svg[svg.IndexOf("<polyline", StringComparison.Ordinal)..]
            .Split("points=\"")[1]
            .Split('"')[0]
            .Split(' ')
            .Length);
    }

    [Fact]
    public void Sparkline_does_not_magnify_tiny_variations()
    {
        // 1〜2ms しか出ない相手を山脈のように描かないこと。
        // 下限スケール 10ms を使うので、最大値でも底までは落ちない
        string svg = SvgSparkline.Render([1, 2, 1]);

        Assert.Contains("<polyline", svg, StringComparison.Ordinal);
        Assert.DoesNotContain(",0 ", svg, StringComparison.Ordinal);   // 上端に張り付いていない
    }
}
