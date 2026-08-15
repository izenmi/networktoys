using System.IO.Compression;
using System.Text;
using PingWatcher.Core.Oui;
using Xunit;

namespace PingWatcher.Core.Tests;

public class OuiLookupTests
{
    private static OuiLookup Build(params string[] lines)
    {
        var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
            gzip.Write(bytes);
        }

        buffer.Position = 0;
        return OuiLookup.Load(buffer);
    }

    private static OuiLookup Sample() => Build(
        "00155D\tMicrosoft",
        "3C22FB\tApple",
        "B827EB\tRaspberry Pi Foundation");

    [Fact]
    public void Empty_lookup_finds_nothing()
    {
        Assert.Equal(0, OuiLookup.Empty.Count);
        Assert.Null(OuiLookup.Empty.Find("00-15-5D-01-02-03"));
    }

    [Fact]
    public void Loads_every_entry()
        => Assert.Equal(3, Sample().Count);

    [Theory]
    [InlineData("00-15-5D-01-02-03", "Microsoft")]
    [InlineData("00:15:5D:01:02:03", "Microsoft")]
    [InlineData("00155D010203", "Microsoft")]
    [InlineData("0015.5d01.0203", "Microsoft")]
    [InlineData("3c22fb000000", "Apple")]
    [InlineData("B8-27-EB-FF-FF-FF", "Raspberry Pi Foundation")]
    public void Finds_the_vendor_regardless_of_separator(string mac, string expected)
        => Assert.Equal(expected, Sample().Find(mac));

    [Fact]
    public void The_prefix_alone_is_enough()
        => Assert.Equal("Apple", Sample().Find("3C-22-FB"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00-15")]           // 短すぎる
    [InlineData("zz-zz-zz-zz")]     // 16 進数ではない
    [InlineData("FF-FF-FF-FF-FF-FF")]  // 未登録
    public void Unknown_input_yields_null(string? mac)
        => Assert.Null(Sample().Find(mac));

    [Fact]
    public void Malformed_lines_are_skipped()
    {
        OuiLookup lookup = Build(
            "00155D\tMicrosoft",
            "これは壊れた行",
            "TOOLONGPREFIX\tだれか",
            string.Empty,
            "3C22FB\tApple");

        Assert.Equal(2, lookup.Count);
        Assert.Equal("Apple", lookup.Find("3C22FB000000"));
    }
}
