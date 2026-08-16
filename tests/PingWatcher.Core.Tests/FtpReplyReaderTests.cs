using PingWatcher.Core.Files;
using PingWatcher.Core.Ftp;
using Xunit;

namespace PingWatcher.Core.Tests;

/// <summary>
/// FTP の応答の組み立てと、接続先のパスの扱い。
///
/// 複数行応答を読み違えると<b>次のコマンドの応答とずれ続ける</b>ので、
/// 1 度おかしくなると以降すべてが狂う。ここは固めておく。
/// </summary>
public class FtpReplyReaderTests
{
    private static FtpReply Read(params string[] lines)
    {
        var reader = new FtpReplyReader();

        for (int i = 0; i < lines.Length - 1; i++)
            Assert.Null(reader.Feed(lines[i]));   // 途中では返らないこと

        FtpReply? reply = reader.Feed(lines[^1]);

        Assert.NotNull(reply);

        return reply!.Value;
    }

    [Fact]
    public void A_single_line_reply_is_returned_at_once()
    {
        FtpReply reply = Read("220 PingWatcher FTP");

        Assert.Equal(220, reply.Code);
        Assert.Equal("PingWatcher FTP", reply.Text);
        Assert.True(reply.IsSuccess);
    }

    [Fact]
    public void A_multiline_reply_waits_for_the_closing_line()
    {
        FtpReply reply = Read(
            "211-Features:",
            " MLSD",
            " SIZE",
            "211 End");

        Assert.Equal(211, reply.Code);
        Assert.Contains("MLSD", reply.Text, StringComparison.Ordinal);
        Assert.Contains("End", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_number_in_the_middle_does_not_end_the_reply()
    {
        // 中の行が数字で始まっていても、コードが同じで空白が続かない限り終わりではない。
        // ここを読み違えると、以降ずっと応答が 1 つずれる
        FtpReply reply = Read(
            "230-ようこそ",
            "230-お知らせ 2026 年の予定",
            "230 ログインしました");

        Assert.Equal(230, reply.Code);
        Assert.Contains("ログインしました", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_code_in_the_middle_does_not_end_the_reply()
    {
        FtpReply reply = Read(
            "211-Features:",
            "250 これは終わりではない",
            "211 End");

        Assert.Equal(211, reply.Code);
    }

    [Fact]
    public void Trailing_newlines_are_trimmed()
        => Assert.Equal("OK", Read("200 OK\r\n").Text);

    [Fact]
    public void A_line_without_a_code_does_not_stall_the_reader()
    {
        // 詰まらせない。コード 0 = 失敗として扱う
        FtpReply reply = Read("ごみ");

        Assert.Equal(0, reply.Code);
        Assert.True(reply.IsFailure);
    }

    [Theory]
    [InlineData(150, false, false, false)]
    [InlineData(226, false, true, false)]
    [InlineData(331, false, false, true)]
    [InlineData(550, true, false, false)]
    [InlineData(0, true, false, false)]
    public void The_first_digit_decides_how_to_read_it(
        int code, bool failure, bool success, bool needsMore)
    {
        var reply = new FtpReply(code, "");

        Assert.Equal(failure, reply.IsFailure);
        Assert.Equal(success, reply.IsSuccess);
        Assert.Equal(needsMore, reply.NeedsMore);
    }

    [Fact]
    public void Reset_throws_away_a_half_built_reply()
    {
        var reader = new FtpReplyReader();

        Assert.Null(reader.Feed("211-Features:"));

        reader.Reset();

        // 組みかけが残っていれば、次の 1 行では返らないはず
        FtpReply? reply = reader.Feed("200 OK");

        Assert.NotNull(reply);
        Assert.Equal(200, reply!.Value.Code);
    }
}

/// <summary>接続先のパスの扱い。相手側のパスなので、ローカルの FS には触らない。</summary>
public class RemotePathTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("//", "/")]
    [InlineData("/home/admin", "/home/admin")]
    [InlineData("home/admin", "/home/admin")]
    [InlineData("/home//admin/", "/home/admin")]
    [InlineData("/home/./admin", "/home/admin")]
    [InlineData("/home/../etc", "/etc")]
    [InlineData("\\home\\admin", "/home/admin")]
    public void A_path_is_folded_into_an_absolute_form(string? given, string expected)
        => Assert.Equal(expected, RemotePath.Normalize(given));

    [Theory]
    [InlineData("/..")]
    [InlineData("/../..")]
    [InlineData("/home/../..")]
    public void Climbing_above_the_root_just_stops_there(string given)
        => Assert.Equal("/", RemotePath.Normalize(given));

    [Theory]
    [InlineData("/home", "admin", "/home/admin")]
    [InlineData("/home", "/etc", "/etc")]
    [InlineData("/", "admin", "/admin")]
    [InlineData("/home/admin", "..", "/home")]
    [InlineData("/home", "", "/home")]
    public void Names_are_appended_to_the_current_place(string current, string name, string expected)
        => Assert.Equal(expected, RemotePath.Combine(current, name));

    [Theory]
    [InlineData("/home/admin", "/home")]
    [InlineData("/home", "/")]
    [InlineData("/", "/")]
    public void The_parent_never_climbs_above_the_root(string given, string expected)
        => Assert.Equal(expected, RemotePath.Parent(given));

    [Theory]
    [InlineData("/home/admin", "admin")]
    [InlineData("/x.txt", "x.txt")]
    [InlineData("/", "")]
    public void The_last_name_can_be_taken_out(string given, string expected)
        => Assert.Equal(expected, RemotePath.Name(given));

    [Fact]
    public void The_parent_row_is_a_directory()
    {
        Assert.True(RemoteEntry.Parent.IsDirectory);
        Assert.True(RemoteEntry.Parent.IsDots);
    }
}
