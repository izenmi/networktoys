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
    public void Only_http_based_checks_are_affected_by_the_proxy()
    {
        // ここを間違えると「プロキシを変えたのに結果が同じ」の説明が付かなくなる。
        // 速度もプロキシ経由で測る（どちらがボトルネックかを見るのが要）
        CheckKind[] throughProxy =
            [CheckKind.Http, CheckKind.Download, CheckKind.Upload, CheckKind.FastCom];

        foreach (CheckKind kind in throughProxy)
            Assert.True(new CheckItem("a", kind, "a").UsesProxy, $"{kind} がプロキシを通らない");

        foreach (CheckKind kind in Enum.GetValues<CheckKind>().Except(throughProxy))
            Assert.False(new CheckItem("a", kind, "a").UsesProxy, $"{kind} がプロキシを通ってしまう");
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

    [Fact]
    public void Every_template_measures_speed()
    {
        // 速度はプロキシの入れ替えでいちばん差が出るところ。ひな型から抜けていると
        // 「入れ替えたら遅くなった」を後から気づくことになる
        foreach ((string name, string text) in RecommendedChecks.Templates)
        {
            IReadOnlyList<CheckItem> items = CheckListParser.Parse(text);

            Assert.Contains(items, i => i.Kind is CheckKind.Download or CheckKind.Upload or CheckKind.FastCom);
        }
    }

    [Fact]
    public void The_fragile_and_the_unprepared_items_stay_commented_out()
    {
        // fast.com は先方の変更で壊れるうえ遮断対象のこともある。
        // 上りは受け取る相手が要る。どちらも既定で走らせない
        IReadOnlyList<CheckItem> items = CheckListParser.Parse(RecommendedChecks.Standard);

        Assert.DoesNotContain(items, i => i.Kind == CheckKind.FastCom);
        Assert.DoesNotContain(items, i => i.Kind == CheckKind.Upload);

        // ただし書き方は残しておく（# を外せば使える）
        Assert.Contains("fast.com", RecommendedChecks.Standard, StringComparison.Ordinal);
        Assert.Contains("速度上り", RecommendedChecks.Standard, StringComparison.Ordinal);
    }

    // ===== PAC の解決結果 =====

    [Theory]
    // WinHTTP が均した形
    [InlineData("10.0.0.10:8080", "http://10.0.0.10:8080")]
    [InlineData("a.example.jp:8080 b.example.jp:8080", "http://a.example.jp:8080")]
    [InlineData("a.example.jp:8080;b.example.jp:8080", "http://a.example.jp:8080")]
    // PAC の書式がそのまま来た場合
    [InlineData("PROXY a.example.jp:8080", "http://a.example.jp:8080")]
    [InlineData("PROXY a.example.jp:8080; DIRECT", "http://a.example.jp:8080")]
    // 直接出るべき場合
    [InlineData("DIRECT", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_first_proxy_from_the_pac_result_is_used(string? list, string expected)
        => Assert.Equal(expected, PacProxy.FirstProxy(list));

    [Fact]
    public void Going_direct_is_said_out_loud()
    {
        // 空欄だと「解決できなかった」のか「直接でよい」のか分からない
        Assert.Contains("直接", PacProxy.Describe(""), StringComparison.Ordinal);
        Assert.Equal("http://a:8080", PacProxy.Describe("http://a:8080"));
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
