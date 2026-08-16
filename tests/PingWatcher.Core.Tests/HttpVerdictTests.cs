using PingWatcher.Core.Verify;
using Xunit;

namespace PingWatcher.Core.Tests;

/// <summary>
/// 応答コードだけで合格にすると、クラウド型プロキシ（Zscaler など）が
/// 200 で返す遮断ページを「合格」と読んでしまう。そこを固める。
/// </summary>
public class HttpVerdictTests
{
    private static readonly CheckItem Plain = new("社内ポータル", CheckKind.Http, "http://portal.example.jp/");

    private static CheckResult Judge(HttpOutcome outcome, CheckItem? item = null)
        => HttpVerdict.Judge(item ?? Plain, "Zscaler", outcome, 120);

    private static HttpOutcome Ok(string body = "", string server = "", string url = "")
        => new(200, url, server, body);

    [Fact]
    public void A_normal_page_passes()
    {
        CheckResult result = Judge(Ok("<html>ようこそ</html>"));

        Assert.True(result.IsPass);
        Assert.Equal("○ 合格", result.VerdictText);
        Assert.Equal("Zscaler", result.ProxyText);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(399)]
    public void Codes_from_2xx_to_3xx_pass(int code)
        => Assert.True(Judge(new HttpOutcome(code, "", "", "")).IsPass);

    [Theory]
    [InlineData(403)]
    [InlineData(407)]   // プロキシの認証が通っていない
    [InlineData(500)]
    public void Other_codes_fail(int code)
    {
        CheckResult result = Judge(new HttpOutcome(code, "", "", ""));

        Assert.True(result.IsFail);
        Assert.Contains(code.ToString(), result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_page_returned_with_200_still_fails()
    {
        // これが本題。コードだけ見ていると通ってしまう
        CheckResult result = Judge(Ok("<html>アクセスがブロックされました</html>"));

        Assert.True(result.IsFail);
        Assert.Contains("遮断ページ", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    // クラウド型
    [InlineData("Access Denied by policy")]
    [InlineData("This web page is Blocked by your organization")]
    [InlineData("zscaler")]
    // オンプレ型
    [InlineData("このページへのアクセスが禁止されています")]
    [InlineData("i-FILTER によりアクセス制限されています")]
    [InlineData("閲覧が制限されているカテゴリです")]
    public void Common_block_wordings_are_caught(string body)
        => Assert.True(Judge(Ok(body)).IsFail);

    [Fact]
    public void An_expected_string_is_the_reliable_check()
    {
        var item = new CheckItem("業務システム", CheckKind.Http, "http://app.example.jp/login", "ログイン");

        Assert.True(Judge(Ok("<html><h1>ログイン</h1></html>"), item).IsPass);

        CheckResult missing = Judge(Ok("<html>アクセスがブロックされました</html>"), item);
        Assert.True(missing.IsFail);
        Assert.Contains("ログイン", missing.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_expected_string_wins_over_the_wording_guess()
    {
        // 「zscaler」という語を含む正規のページもありうる。
        // 期待する文字列が書いてあるなら、そちらだけで判断する
        var item = new CheckItem("手順書", CheckKind.Http, "http://portal.example.jp/zscaler", "設定手順");

        Assert.True(Judge(Ok("zscaler の設定手順はこちら"), item).IsPass);
    }

    [Fact]
    public void Who_answered_is_always_recorded()
    {
        // 後から「誰が返したのか」を追えないと証跡にならない
        CheckResult result = Judge(Ok("ok", server: "ZscalerProxy", url: "https://portal.example.jp/top"));

        Assert.Contains("ZscalerProxy", result.Detail, StringComparison.Ordinal);
        Assert.Contains("https://portal.example.jp/top", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_connection_failure_reports_the_reason()
    {
        CheckResult result = Judge(new HttpOutcome(0, "", "", "", "名前を解決できませんでした"));

        Assert.True(result.IsFail);
        Assert.Equal("名前を解決できませんでした", result.Detail);
    }

    // ===== バナー =====

    [Theory]
    [InlineData(CheckKind.Smtp, "220 mail.example.jp ESMTP", true)]
    [InlineData(CheckKind.Smtp, "554 no service", false)]
    [InlineData(CheckKind.Imap, "* OK [CAPABILITY IMAP4rev1] ready", true)]
    [InlineData(CheckKind.Imap, "* BAD", false)]
    [InlineData(CheckKind.Pop3, "+OK POP3 ready", true)]
    [InlineData(CheckKind.Pop3, "-ERR", false)]
    // 先頭の空白は落とす
    [InlineData(CheckKind.Smtp, "  220 ready", true)]
    // 途中で切れていても、名乗ってさえいれば通す
    [InlineData(CheckKind.Smtp, "220", true)]
    [InlineData(CheckKind.Smtp, "", false)]
    public void Banners_decide_whether_the_mail_server_answered(CheckKind kind, string banner, bool expected)
        => Assert.Equal(expected, BannerCheck.Matches(kind, banner));

    [Fact]
    public void A_long_banner_is_trimmed_for_the_record()
    {
        string summary = BannerCheck.Summarize(new string('a', 200));

        Assert.Equal(81, summary.Length);
        Assert.EndsWith("…", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_banner_keeps_to_one_line()
        => Assert.Equal("220 ready 250 ok", BannerCheck.Summarize("220 ready\r\n250 ok"));

    // ===== まとめ =====

    [Fact]
    public void The_summary_says_how_many_failed()
    {
        CheckResult[] results =
        [
            Judge(Ok("ok")),
            Judge(new HttpOutcome(403, "", "", "")),
            new("skip", CheckKind.Tcp, "", "", CheckVerdict.Skipped, ""),
        ];

        string summary = CheckReport.Summarize(results);

        Assert.Contains("不合格が 1 件", summary, StringComparison.Ordinal);
        Assert.Contains("試験せず 1", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_csv_has_a_column_for_which_proxy_was_used()
    {
        var table = CheckReport.ToCsv([Judge(Ok("ok"))]);

        Assert.Contains("プロキシ", table.Headers);
        Assert.Contains("Zscaler", table.Rows[0]);
    }
}
