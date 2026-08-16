using PingWatcher.Core.Verify;
using Xunit;

namespace PingWatcher.Core.Tests;

public class SpeedVerdictTests
{
    private static readonly CheckItem Plain = new("回線速度", CheckKind.Download, "https://example.jp/large.bin");

    private static SpeedSample Mb(double megabytes, double seconds)
        => new((long)(megabytes * 1024 * 1024), seconds * 1000);

    // ===== 速度の判定 =====

    [Fact]
    public void Without_a_guideline_it_just_reports_the_number()
    {
        CheckResult result = SpeedVerdict.Judge(Plain, "直接", Mb(100, 2));

        Assert.True(result.IsPass);
        // 測った値は必ず残す
        Assert.Contains("MB/s", result.Detail, StringComparison.Ordinal);
        Assert.Contains("100 MB", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Meeting_the_guideline_passes()
    {
        var item = Plain with { Expect = "20" };

        // 100MB を 2 秒 = 50 MB/s
        Assert.True(SpeedVerdict.Judge(item, "Zscaler", Mb(100, 2)).IsPass);
    }

    [Fact]
    public void Falling_short_is_a_warning_not_a_failure()
    {
        // 遅くても繋がってはいる。不合格にすると疎通の可否と混ざる
        var item = Plain with { Expect = "20" };

        CheckResult result = SpeedVerdict.Judge(item, "Zscaler", Mb(10, 2));

        Assert.True(result.IsWarn);
        Assert.False(result.IsFail);
        Assert.Contains("目安 20 MB/s を下回っています", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("20", 20.0)]
    [InlineData("20MB/s", 20.0)]
    [InlineData("20 MB", 20.0)]
    [InlineData("2.5", 2.5)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("はやい", null)]
    [InlineData("0", null)]      // 0 は目安にならない
    [InlineData("-5", null)]
    public void The_guideline_is_read_from_the_expectation_column(string expect, double? expected)
        => Assert.Equal(expected, SpeedVerdict.ParseExpected(expect));

    [Fact]
    public void A_failure_to_measure_is_a_failure()
    {
        CheckResult result = SpeedVerdict.Judge(Plain, "直接", new SpeedSample(0, 0, "HTTP 403 が返りました"));

        Assert.True(result.IsFail);
        Assert.Equal("HTTP 403 が返りました", result.Detail);
    }

    [Fact]
    public void Nothing_transferred_is_a_failure()
        => Assert.True(SpeedVerdict.Judge(Plain, "直接", new SpeedSample(0, 1000)).IsFail);

    [Fact]
    public void Zero_elapsed_does_not_divide_by_zero()
        => Assert.Equal(0, new SpeedSample(1000, 0).BytesPerSecond);

    [Fact]
    public void Speed_checks_go_through_the_proxy()
    {
        // どちらがボトルネックかを見るのが要なので、ここが false だと意味が無くなる
        foreach (CheckKind kind in new[] { CheckKind.Download, CheckKind.Upload, CheckKind.FastCom })
            Assert.True(new CheckItem("a", kind, "").UsesProxy);
    }

    // ===== fast.com の解析 =====

    [Fact]
    public void The_script_path_is_found_in_the_page()
    {
        const string html = """<html><script src="/app-a1b2c3d4e5.js"></script></html>""";

        Assert.Equal("/app-a1b2c3d4e5.js", FastComPlan.FindScriptPath(html));
    }

    [Theory]
    [InlineData("""token:"YXNkZmFzZGZhc2Rm" """, "YXNkZmFzZGZhc2Rm")]
    [InlineData("""token: 'abc123' """, "abc123")]
    [InlineData("""{token:"xyz"}""", "xyz")]
    public void The_token_is_found_in_the_script(string script, string expected)
        => Assert.Equal(expected, FastComPlan.FindToken(script));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<html>先方の作りが変わった</html>")]
    public void A_changed_page_yields_nothing_rather_than_a_wrong_guess(string? html)
    {
        // 先方が変えたら「取れない」と言うのが正しい。適当な値を返してはいけない
        Assert.Null(FastComPlan.FindScriptPath(html));
        Assert.Null(FastComPlan.FindToken(html));
    }

    [Fact]
    public void Measurement_urls_are_taken_from_the_api_answer()
    {
        const string json = """
            {"client":{"ip":"203.0.113.9"},
             "targets":[{"url":"https://a.example/x"},{"url":"https://b.example/y"}]}
            """;

        Assert.Equal(["https://a.example/x", "https://b.example/y"], FastComPlan.ParseTargets(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"targets":[]}""")]
    [InlineData("""{"targets":"文字列だった"}""")]
    [InlineData("これは JSON ではない")]
    public void An_unusable_answer_gives_no_urls_instead_of_throwing(string json)
        => Assert.Empty(FastComPlan.ParseTargets(json));

    [Fact]
    public void The_api_url_carries_the_token()
    {
        string url = FastComPlan.BuildApiUrl("ab+cd", 3);

        Assert.Contains("token=ab%2Bcd", url, StringComparison.Ordinal);
        Assert.Contains("urlCount=3", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_step_has_its_own_wording()
    {
        // どこで躓いたかが分からないと、先方の変更なのか経路の問題なのか切り分けられない
        string[] messages =
            [.. Enum.GetValues<FastComStep>().Select(FastComPlan.DescribeFailure)];

        Assert.Equal(messages.Length, messages.Distinct().Count());
    }
}
