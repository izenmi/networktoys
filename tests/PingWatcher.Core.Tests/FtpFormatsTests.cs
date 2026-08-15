using System.Globalization;
using System.Net;
using PingWatcher.Core.Ftp;
using Xunit;

namespace PingWatcher.Core.Tests;

public class FtpFormatsTests
{
    [Fact]
    public void Passive_mode_encodes_the_port_as_two_bytes()
    {
        // 50000 = 195*256 + 80
        string reply = FtpFormats.PassiveModeReply(IPAddress.Parse("192.168.1.10"), 50000);

        Assert.Equal("227 Entering Passive Mode (192,168,1,10,195,80).", reply);
    }

    [Fact]
    public void The_port_command_round_trips()
    {
        (IPAddress Address, int Port)? parsed = FtpFormats.ParsePort("192,168,1,10,195,80");

        Assert.NotNull(parsed);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), parsed.Value.Address);
        Assert.Equal(50000, parsed.Value.Port);
    }

    [Theory]
    [InlineData("192,168,1,10,195")]      // 少ない
    [InlineData("192,168,1,10,195,80,0")] // 多い
    [InlineData("192,168,1,999,195,80")]  // 範囲外
    [InlineData("")]
    public void A_broken_port_command_is_null(string argument)
    {
        Assert.Null(FtpFormats.ParsePort(argument));
    }

    [Fact]
    public void A_list_line_uses_an_english_month_regardless_of_culture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ja-JP");

            string line = FtpFormats.ListLine(new FtpListEntry(
                "running-config", IsDirectory: false, Size: 1234,
                Modified: new DateTime(2026, 8, 15, 9, 30, 0)));

            Assert.Contains("Aug 15 09:30", line, StringComparison.Ordinal);
            Assert.EndsWith("running-config", line, StringComparison.Ordinal);
            Assert.StartsWith("-rw-r--r--", line, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_directory_line_starts_with_d()
    {
        string line = FtpFormats.ListLine(new FtpListEntry(
            "logs", IsDirectory: true, Size: 0, Modified: new DateTime(2026, 8, 15, 9, 30, 0)));

        Assert.StartsWith("drwxr-xr-x", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quoted_path_doubles_inner_quotes()
    {
        Assert.Equal("\"/a \"\"b\"\" c\"", FtpFormats.QuotedPath("/a \"b\" c"));
    }
}
