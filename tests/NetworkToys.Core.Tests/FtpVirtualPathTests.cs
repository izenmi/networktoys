using NetworkToys.Core.Ftp;
using Xunit;

namespace NetworkToys.Core.Tests;

public class FtpVirtualPathTests
{
    private static readonly string Root =
        OperatingSystem.IsWindows() ? @"C:\ftproot" : "/ftproot";

    private static FtpVirtualPath New() => new(Root);

    [Fact]
    public void It_starts_at_the_root()
    {
        Assert.Equal("/", New().CurrentDirectory);
    }

    [Fact]
    public void A_plain_name_resolves_inside_the_root()
    {
        string? local = New().Resolve("config.txt");

        Assert.NotNull(local);
        Assert.StartsWith(Root, local);
        Assert.EndsWith("config.txt", local);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/../secret")]
    [InlineData("a/../../secret")]
    [InlineData("..\\..\\secret")]
    public void Escaping_the_root_is_refused(string argument)
    {
        Assert.Null(New().Resolve(argument));
    }

    [Theory]
    [InlineData("C:\\Windows\\system32")]
    [InlineData("/etc/passwd/../..")]
    public void Absolute_and_drive_paths_cannot_break_out(string argument)
    {
        // 絶対 /etc/passwd 自体はルート内の /etc/passwd に写るが、
        // そこから .. で抜けようとすると弾かれる。ドライブ文字は常に拒否
        string? local = New().Resolve(argument);
        Assert.True(local is null || local.StartsWith(Root, StringComparison.Ordinal));
    }

    [Fact]
    public void Going_to_the_parent_never_climbs_above_the_root()
    {
        var path = New();

        path.GoToParent();
        path.GoToParent();

        Assert.Equal("/", path.CurrentDirectory);
    }

    [Fact]
    public void A_backslash_is_treated_like_a_slash()
    {
        // Windows のクライアントは \ を送ってくることがある
        string? a = New().Resolve("sub\\file.txt");
        string? b = New().Resolve("sub/file.txt");

        Assert.Equal(b, a);
    }
}
