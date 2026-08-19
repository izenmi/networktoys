using NetworkToys.Core.Verify;
using Xunit;

namespace NetworkToys.Core.Tests;

public class DohLookupTests
{
    [Fact]
    public void 問い合わせのURLはAレコードを求める()
    {
        string url = DohLookup.UrlFor(DohLookup.GoogleResolver, "worldaz.tr.teams.microsoft.com");

        Assert.Equal("https://dns.google/resolve?name=worldaz.tr.teams.microsoft.com&type=A", url);
    }

    [Fact]
    public void 応答からAレコードだけを取り出す()
    {
        const string json = """
            {"Status":0,"Answer":[
              {"name":"worldaz.tr.teams.microsoft.com.","type":5,"TTL":30,"data":"tr.teams.microsoft.com."},
              {"name":"tr.teams.microsoft.com.","type":1,"TTL":30,"data":"52.113.194.132"},
              {"name":"tr.teams.microsoft.com.","type":1,"TTL":30,"data":"52.114.188.31"}]}
            """;

        Assert.Equal(["52.113.194.132", "52.114.188.31"], DohLookup.ReadAddresses(json));
    }

    [Theory]
    // 引けなかった応答（NXDOMAIN や SERVFAIL）は Answer を持たない
    [InlineData("""{"Status":2}""")]
    // 遮断ページのような、そもそも JSON でないもの
    [InlineData("<html>blocked</html>")]
    [InlineData("")]
    [InlineData(null)]
    // アドレスとして読めない値は捨てる
    [InlineData("""{"Answer":[{"type":1,"data":"not-an-address"}]}""")]
    public void 読めない応答は空で返す例外にしない(string? json)
        => Assert.Empty(DohLookup.ReadAddresses(json));
}
