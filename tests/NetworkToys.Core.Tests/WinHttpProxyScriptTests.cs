using NetworkToys.Core.Net;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// WinHTTP のプロキシ設定を netsh のスクリプト行にするところ。
///
/// <b>この文字列は昇格して実行される。</b>組み立てを誤ると PC 全体の設定が壊れるか、
/// 最悪、意図しないコマンドが管理者権限で走る。ここで固める。
/// </summary>
public class WinHttpProxyScriptTests
{
    [Fact]
    public void A_fixed_proxy_becomes_a_set_command()
    {
        var plan = new ProxyPlan(ProxyMode.Fixed, "", "proxy.example.jp:8080", "<local>");

        Assert.Equal(
            ["""winhttp set proxy proxy-server="proxy.example.jp:8080" bypass-list="<local>" """.TrimEnd()],
            WinHttpProxyScript.Build(plan));
    }

    [Fact]
    public void An_empty_bypass_leaves_the_option_out()
    {
        var plan = new ProxyPlan(ProxyMode.Fixed, "", "proxy.example.jp:8080", "");

        Assert.Equal(["""winhttp set proxy proxy-server="proxy.example.jp:8080" """.TrimEnd()],
                     WinHttpProxyScript.Build(plan));
    }

    [Theory]
    [InlineData(ProxyMode.None)]
    [InlineData(ProxyMode.Pac)]   // WinHTTP は PAC を持てないので、解除に倒す
    public void Anything_but_a_fixed_proxy_resets(ProxyMode mode)
        => Assert.Equal(["winhttp reset proxy"],
                        WinHttpProxyScript.Build(new ProxyPlan(mode, "http://x/p.pac", "", "")));

    // ===== 組み立ての安全 =====

    [Theory]
    [InlineData("""proxy:8080" bypass-list="x""", "proxy:8080 bypass-list=x")]
    [InlineData("proxy:8080\r\nwinhttp reset proxy", "proxy:8080winhttp reset proxy")]
    public void Quotes_and_newlines_cannot_break_out(string given, string expected)
        => Assert.Equal(expected, WinHttpProxyScript.Sanitize(given));

    [Theory]
    [InlineData("<local>")]
    [InlineData("<local>;*.example.jp")]
    [InlineData("a&b|c^d")]
    public void Everything_else_is_left_alone(string given)
    {
        // netsh はスクリプトを自分で読む(コマンドプロンプトを経由しない)ので
        // これらに特別な意味は無い。<local> は除外リストで最もよく使う値
        Assert.Equal(given, WinHttpProxyScript.Sanitize(given));
    }

    [Fact]
    public void The_built_line_never_contains_a_stray_quote()
    {
        var plan = new ProxyPlan(ProxyMode.Fixed, "", """evil:8080" bypass-list="all""", "");

        string line = WinHttpProxyScript.Build(plan)[0];

        // 引用符は自分で足した 2 つだけ。入力側の分が残っていたら閉じられている
        Assert.Equal(2, line.Count(c => c == '"'));
    }

    [Fact]
    public void Sanitize_handles_nothing()
    {
        Assert.Equal("", WinHttpProxyScript.Sanitize(null));
        Assert.Equal("", WinHttpProxyScript.Sanitize("   "));
    }

    // ===== 画面に出す 1 行 =====

    [Theory]
    [InlineData(true, "proxy:8080", "", "プロキシなし（直接接続）")]
    [InlineData(false, "", "", "プロキシなし（直接接続）")]
    [InlineData(false, "proxy:8080", "", "固定: proxy:8080")]
    [InlineData(false, "proxy:8080", "<local>", "固定: proxy:8080（除外 <local>）")]
    public void The_summary_says_what_is_set(bool direct, string server, string bypass, string expected)
        => Assert.Equal(expected, WinHttpProxyScript.Describe(direct, server, bypass));
}
