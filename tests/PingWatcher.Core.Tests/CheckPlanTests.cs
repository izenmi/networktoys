using PingWatcher.Core.Verify;
using Xunit;

namespace PingWatcher.Core.Tests;

public class CheckPlanTests
{
    // ===== 試験項目のテキスト =====

    [Fact]
    public void Items_round_trip_through_text()
    {
        const string text = """
            # 注釈は読み飛ばす
            社内ポータル,HTTP,http://portal.example.jp/,ログイン
            ファイルサーバ,TCP,fs01:445
            Teams,Teams
            """;

        IReadOnlyList<CheckItem> items = CheckListParser.Parse(text);

        Assert.Equal(3, items.Count);
        Assert.Equal(new CheckItem("社内ポータル", CheckKind.Http, "http://portal.example.jp/", "ログイン"), items[0]);
        Assert.Equal(new CheckItem("ファイルサーバ", CheckKind.Tcp, "fs01:445"), items[1]);
        Assert.Equal(new CheckItem("Teams", CheckKind.Teams, ""), items[2]);

        Assert.Equal(items, CheckListParser.Parse(CheckListParser.Format(items)));
    }

    [Fact]
    public void The_expectation_may_contain_commas()
    {
        // 分割は前から 3 回だけ。4 つ目には残り全部が入る
        CheckItem item = Assert.Single(CheckListParser.Parse("名前,HTTP,http://a/,あれ,これ,それ"));

        Assert.Equal("あれ,これ,それ", item.Expect);
    }

    [Theory]
    [InlineData("項目だけ")]                    // 種類が無い
    [InlineData("項目,知らない種類,宛先")]      // 種類が読めない
    [InlineData("")]
    [InlineData("   ")]
    public void Lines_that_cannot_be_understood_are_dropped(string line)
        => Assert.Empty(CheckListParser.Parse(line));

    [Theory]
    [InlineData("# 注釈")]
    [InlineData("; 注釈")]
    [InlineData("' 注釈")]
    [InlineData("　全角スペース始まり")]
    public void Comment_lines_are_skipped(string line)
        => Assert.Empty(CheckListParser.Parse(line));

    [Theory]
    [InlineData("http", CheckKind.Http)]
    [InlineData("HTTPS", CheckKind.Http)]
    [InlineData("Teams", CheckKind.Teams)]
    [InlineData("pop", CheckKind.Pop3)]
    public void Kind_names_are_case_insensitive(string text, CheckKind expected)
    {
        Assert.True(CheckListParser.TryParseKind(text, out CheckKind kind));
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void Only_http_is_affected_by_the_proxy()
    {
        // ここを間違えると「プロキシを変えたのに結果が同じ」の説明が付かなくなる
        Assert.True(new CheckItem("a", CheckKind.Http, "http://a/").UsesProxy);

        foreach (CheckKind kind in Enum.GetValues<CheckKind>().Where(k => k != CheckKind.Http))
            Assert.False(new CheckItem("a", kind, "a").UsesProxy);
    }

    // ===== 宛先の分解 =====

    [Theory]
    [InlineData("fs01:445", CheckKind.Tcp, "fs01", 445)]
    [InlineData("fs01", CheckKind.Tcp, "fs01", 0)]
    // 種類ごとの既定ポート
    [InlineData("mail.example.jp", CheckKind.Smtp, "mail.example.jp", 587)]
    [InlineData("mail.example.jp", CheckKind.Imap, "mail.example.jp", 993)]
    [InlineData("mail.example.jp", CheckKind.Pop3, "mail.example.jp", 995)]
    [InlineData("mail.example.jp:25", CheckKind.Smtp, "mail.example.jp", 25)]
    // 素の IPv6 は切らない（コロンが 1 つのときだけ切る）
    [InlineData("2001:db8::1", CheckKind.Tcp, "2001:db8::1", 0)]
    [InlineData("[2001:db8::1]:445", CheckKind.Tcp, "2001:db8::1", 445)]
    [InlineData("[2001:db8::1]", CheckKind.Imap, "2001:db8::1", 993)]
    // 範囲外のポートは無視して既定へ
    [InlineData("fs01:99999", CheckKind.Smtp, "fs01:99999", 587)]
    public void Targets_split_into_host_and_port(string target, CheckKind kind, string host, int port)
        => Assert.Equal((host, port), CheckListParser.SplitTarget(target, kind));

    // ===== ひな型 =====

    [Fact]
    public void Every_template_parses_and_uses_only_reserved_domains()
    {
        foreach ((string name, string text) in RecommendedChecks.Templates)
        {
            IReadOnlyList<CheckItem> items = CheckListParser.Parse(text);

            Assert.True(items.Count > 0, $"{name} が 1 件も読めない");

            // 書き換え忘れで実在の他人へ試験を投げないよう、例示は予約ドメインに限る。
            // Microsoft 365 のひな型だけは実在の宛先でよい（公開されている経路のため）
            if (name.Contains("365", StringComparison.Ordinal)) continue;

            foreach (CheckItem item in items)
            {
                Assert.True(item.Target.Length == 0 || item.Target.Contains("example.jp", StringComparison.Ordinal),
                            $"{name} の「{item.Name}」が予約ドメインでない: {item.Target}");
            }
        }
    }

    // ===== プロキシ =====

    [Fact]
    public void Direct_and_the_current_setting_are_always_offered()
    {
        IReadOnlyList<ProxyChoice> list = ProxyListParser.Parse(null);

        Assert.Equal(2, list.Count);
        Assert.Equal(ProxyMode.Direct, list[0].Mode);
        Assert.Equal(ProxyMode.System, list[1].Mode);
    }

    [Fact]
    public void Proxies_round_trip_through_text()
    {
        const string text = """
            # 現場のプロキシ
            新プロキシ,pac,http://pac.example.jp/proxy.pac
            Zscaler,pac,http://pac.zscaler.net/example/proxy.pac
            旧プロキシ,proxy,10.0.0.10:8080
            """;

        IReadOnlyList<ProxyChoice> list = ProxyListParser.Parse(text);

        // 先頭 2 つは常にある分
        Assert.Equal(5, list.Count);
        Assert.Equal(new ProxyChoice("新プロキシ", ProxyMode.Pac, "http://pac.example.jp/proxy.pac"), list[2]);
        Assert.Equal(new ProxyChoice("Zscaler", ProxyMode.Pac, "http://pac.zscaler.net/example/proxy.pac"), list[3]);

        // アドレスの書き方が省略形でも通る
        Assert.Equal("http://10.0.0.10:8080", list[4].Address);

        Assert.Equal(list, ProxyListParser.Parse(ProxyListParser.Format(list)));
    }

    [Theory]
    [InlineData("名前だけ")]
    [InlineData("名前,proxy")]              // アドレスが無い
    [InlineData("名前,proxy,")]             // 空のアドレスは「直接」と区別が付かない
    [InlineData("名前,pac,")]
    [InlineData("名前,知らない種類,10.0.0.1:8080")]
    public void Proxy_lines_that_cannot_be_understood_are_dropped(string line)
        => Assert.Equal(2, ProxyListParser.Parse(line).Count);

    [Fact]
    public void A_duplicated_proxy_name_is_dropped()
    {
        // 同じ名前が証跡に並ぶと、どちらの結果か分からなくなる
        IReadOnlyList<ProxyChoice> list = ProxyListParser.Parse(
            "同じ,proxy,10.0.0.1:8080\n同じ,proxy,10.0.0.2:8080\n直接,proxy,10.0.0.3:8080");

        Assert.Equal(3, list.Count);
        Assert.Equal("http://10.0.0.1:8080", list[2].Address);
    }
}
