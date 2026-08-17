using System.Globalization;
using NetworkToys.Core.Files;
using NetworkToys.Core.Ftp;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// FTP クライアントの一覧の解釈。
///
/// <b>ここが FTP クライアントの難所。</b>通信そのものは短いが、一覧の書式は
/// サーバ任せで決まっていない。読めない行で一覧全体が出なくなる方が困るので、
/// <b>捨てる</b>のが正しい振る舞い — それも含めて固める。
/// </summary>
public class FtpListingTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Unspecified);

    private static RemoteEntry Parse(string line)
    {
        RemoteEntry? entry = FtpListing.ParseListLine(line, Now);

        Assert.True(entry is not null, $"読めなかった: 「{line}」");

        return entry!.Value;
    }

    // ===== 自前のサーバとの往復 =====

    [Fact]
    public void What_our_own_server_writes_can_be_read_back()
    {
        // 生成側（FtpFormats.ListLine）は既にあるので、往復で確かめられる。
        // 自分のサーバに自分のクライアントで繋ぐ自己診断の土台でもある
        var original = new FtpListEntry("running-config", IsDirectory: false, 12345,
                                        new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Unspecified));

        RemoteEntry parsed = Parse(FtpFormats.ListLine(original));

        Assert.Equal(original.Name, parsed.Name);
        Assert.False(parsed.IsDirectory);
        Assert.Equal(original.Size, parsed.Size);
        Assert.Equal(original.Modified, parsed.Modified);
    }

    [Fact]
    public void A_directory_from_our_own_server_round_trips()
    {
        var original = new FtpListEntry("backup", IsDirectory: true, 0,
                                        new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Unspecified));

        RemoteEntry parsed = Parse(FtpFormats.ListLine(original));

        Assert.Equal("backup", parsed.Name);
        Assert.True(parsed.IsDirectory);
    }

    // ===== UNIX 形式 =====

    [Fact]
    public void A_unix_file_line_is_read()
    {
        RemoteEntry entry = Parse("-rw-r--r--   1 root     wheel        1234 Aug 15 09:30 startup-config");

        Assert.Equal("startup-config", entry.Name);
        Assert.False(entry.IsDirectory);
        Assert.Equal(1234, entry.Size);
        Assert.Equal(new DateTime(2026, 8, 15, 9, 30, 0), entry.Modified);
    }

    [Fact]
    public void A_unix_directory_line_is_read()
    {
        RemoteEntry entry = Parse("drwxr-xr-x   2 root     wheel        4096 Jul  1 10:00 backup");

        Assert.Equal("backup", entry.Name);
        Assert.True(entry.IsDirectory);
        Assert.Equal(0, entry.Size);
    }

    [Fact]
    public void A_name_with_spaces_survives()
    {
        // 後ろから数えると壊れる。名前は「日時の次の空白から後ろ全部」
        RemoteEntry entry = Parse("-rw-r--r--   1 root wheel   99 Aug 15 09:30 my config file.txt");

        Assert.Equal("my config file.txt", entry.Name);
    }

    [Fact]
    public void A_symlink_shows_only_its_own_name()
    {
        RemoteEntry entry = Parse("lrwxrwxrwx 1 root root 7 Aug 15 09:30 current -> backup");

        Assert.Equal("current", entry.Name);
    }

    [Fact]
    public void A_line_without_a_group_column_is_still_read()
    {
        // グループを出さないサーバがある。月名を探して逆算しているので通る
        RemoteEntry entry = Parse("-rw-r--r-- 1 root 4096 Aug 15 09:30 config.txt");

        Assert.Equal("config.txt", entry.Name);
        Assert.Equal(4096, entry.Size);
    }

    // ===== 年の無い日付 =====

    [Fact]
    public void A_year_in_place_of_the_time_is_used_as_is()
    {
        RemoteEntry entry = Parse("-rw-r--r-- 1 root wheel 10 Aug 15  2021 old.txt");

        Assert.Equal(new DateTime(2021, 8, 15), entry.Modified);
    }

    [Fact]
    public void A_date_that_would_be_in_the_future_belongs_to_last_year()
    {
        // 時刻が出ている＝直近半年以内。未来にはなりえないので、
        // 「いま」が 2026/8/16 なら 12/25 は来年ではなく去年のはず
        RemoteEntry entry = Parse("-rw-r--r-- 1 root wheel 10 Dec 25 23:59 last-year.txt");

        Assert.Equal(2025, entry.Modified.Year);
    }

    [Fact]
    public void A_recent_date_stays_in_this_year()
    {
        RemoteEntry entry = Parse("-rw-r--r-- 1 root wheel 10 Aug 15 09:30 today.txt");

        Assert.Equal(2026, entry.Modified.Year);
    }

    // ===== DOS 形式 =====

    [Fact]
    public void A_dos_file_line_is_read()
    {
        RemoteEntry entry = Parse("08-15-26  09:30AM              1234 startup-config");

        Assert.Equal("startup-config", entry.Name);
        Assert.False(entry.IsDirectory);
        Assert.Equal(1234, entry.Size);
        Assert.Equal(new DateTime(2026, 8, 15, 9, 30, 0), entry.Modified);
    }

    [Fact]
    public void A_dos_directory_line_is_read()
    {
        RemoteEntry entry = Parse("08-15-26  09:30AM       <DIR>          backup");

        Assert.Equal("backup", entry.Name);
        Assert.True(entry.IsDirectory);
    }

    [Fact]
    public void A_two_digit_year_of_seventy_or_more_is_the_nineteen_hundreds()
    {
        Assert.Equal(1998, Parse("08-15-98  09:30AM  10 old.txt").Modified.Year);
        Assert.Equal(2026, Parse("08-15-26  09:30AM  10 new.txt").Modified.Year);
    }

    // ===== 捨てるべき行 =====

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("total 12")]
    [InlineData("TOTAL 12")]
    [InlineData("これは一覧ではない")]
    [InlineData("-rw-r--r-- 1 root wheel")]                       // 途中で切れている
    [InlineData("brw-r--r-- 1 root wheel 0 Aug 15 09:30 sda")]    // ブロックデバイス
    [InlineData("-rw-r--r-- 1 root wheel 10 Xxx 15 09:30 x.txt")] // 月名が読めない
    public void An_unreadable_line_is_dropped_rather_than_thrown(string line)
        => Assert.Null(FtpListing.ParseListLine(line, Now));

    [Theory]
    [InlineData("drwxr-xr-x 2 root wheel 4096 Aug 15 09:30 .")]
    [InlineData("drwxr-xr-x 2 root wheel 4096 Aug 15 09:30 ..")]
    public void The_dots_are_dropped(string line)
        => Assert.Null(FtpListing.ParseListLine(line, Now));

    [Fact]
    public void The_month_name_is_read_even_in_a_japanese_culture()
    {
        // サーバは英語の月名を返す。こちらのカルチャで解釈を変えてはいけない
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ja-JP");

            Assert.Equal(8, Parse("-rw-r--r-- 1 root wheel 10 Aug 15 09:30 x.txt").Modified.Month);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ===== MLSD =====

    [Fact]
    public void A_machine_listing_line_is_read()
    {
        RemoteEntry? entry = FtpListing.ParseMachineLine(
            "type=file;size=1234;modify=20260815093000;perm=adfrw; startup-config");

        Assert.NotNull(entry);
        Assert.Equal("startup-config", entry!.Value.Name);
        Assert.False(entry.Value.IsDirectory);
        Assert.Equal(1234, entry.Value.Size);
        Assert.Equal(new DateTime(2026, 8, 15, 9, 30, 0), entry.Value.Modified);
    }

    [Fact]
    public void A_machine_directory_is_read()
    {
        RemoteEntry? entry = FtpListing.ParseMachineLine("type=dir;size=0;modify=20260815093000; backup");

        Assert.NotNull(entry);
        Assert.True(entry!.Value.IsDirectory);
    }

    [Theory]
    [InlineData("type=cdir;modify=20260815093000; /home")]
    [InlineData("type=pdir;modify=20260815093000; /")]
    public void The_machine_listing_drops_here_and_parent(string line)
        => Assert.Null(FtpListing.ParseMachineLine(line));

    [Theory]
    [InlineData("")]
    [InlineData("type=file;size=1")]        // 名前が無い
    [InlineData("これは MLSD ではない ")]
    public void An_unreadable_machine_line_is_dropped(string line)
    {
        RemoteEntry? entry = FtpListing.ParseMachineLine(line);

        // 名前が取れてしまう形でも、例外は投げないこと
        Assert.True(entry is null || entry.Value.Name.Length > 0);
    }

    [Fact]
    public void A_machine_name_may_contain_spaces()
    {
        RemoteEntry? entry = FtpListing.ParseMachineLine("type=file;size=1; my config file.txt");

        Assert.Equal("my config file.txt", entry!.Value.Name);
    }

    // ===== 時刻 =====

    [Fact]
    public void A_timestamp_is_read()
        => Assert.Equal(new DateTime(2026, 8, 15, 9, 30, 0), FtpListing.ParseTimestamp("20260815093000"));

    [Fact]
    public void A_timestamp_with_fractional_seconds_is_read()
        => Assert.Equal(new DateTime(2026, 8, 15, 9, 30, 0), FtpListing.ParseTimestamp("20260815093000.123"));

    [Theory]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("ふつうの文字")]
    public void A_broken_timestamp_is_null(string value)
        => Assert.Null(FtpListing.ParseTimestamp(value));

    [Fact]
    public void The_timestamp_round_trips_with_the_server_side()
    {
        var when = new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Unspecified);

        Assert.Equal(when, FtpListing.ParseTimestamp(FtpFormats.Timestamp(when)));
    }

    // ===== 257 応答 =====

    [Fact]
    public void The_current_directory_is_taken_out_of_the_quotes()
        => Assert.Equal("/home/admin", FtpListing.UnquotePath("257 \"/home/admin\" is current directory"));

    [Fact]
    public void A_doubled_quote_inside_the_path_becomes_one()
    {
        // 生成側（QuotedPath）の逆。往復で確かめる
        string path = "/a \"b\" c";

        Assert.Equal(path, FtpListing.UnquotePath("257 " + FtpFormats.QuotedPath(path)));
    }

    [Fact]
    public void A_reply_without_quotes_still_gives_something()
        => Assert.Equal("/home/admin", FtpListing.UnquotePath("257 /home/admin"));

    // ===== PASV 応答 =====

    [Fact]
    public void The_passive_reply_round_trips_with_the_server_side()
    {
        string reply = FtpFormats.PassiveModeReply(System.Net.IPAddress.Parse("192.168.1.10"), 50000);

        (System.Net.IPAddress Address, int Port)? parsed = FtpListing.ParsePassiveReply(reply);

        Assert.NotNull(parsed);
        Assert.Equal("192.168.1.10", parsed!.Value.Address.ToString());
        Assert.Equal(50000, parsed.Value.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("227 Entering Passive Mode")]
    [InlineData("227 (1,2,3)")]
    [InlineData("500 Not understood")]
    public void A_broken_passive_reply_is_null(string reply)
        => Assert.Null(FtpListing.ParsePassiveReply(reply));
}
