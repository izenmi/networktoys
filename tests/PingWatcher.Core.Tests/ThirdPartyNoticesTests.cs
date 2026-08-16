using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PingWatcher.Core.Tests;

/// <summary>
/// 同梱物の著作権表示が、実際の依存と食い違っていないことを確かめる。
///
/// <b>パッケージを足して通知を書き忘れる</b>という事故は、これでしか防げない
/// （ビルドも自己診断も通ってしまう）。MIT / Apache-2.0 / OFL 1.1 はいずれも
/// 再配布時に著作権表示とライセンス本文を添えることを求めているので、
/// 書き忘れはライセンス違反そのものになる。
/// </summary>
public class ThirdPartyNoticesTests
{
    private static string Root()
    {
        // テストの出力先は bin/Debug/net10.0 の下。リポジトリの目印を上へ辿って探す
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PingWatcher.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "リポジトリのルート（PingWatcher.slnx）が見つからない");

        return dir!.FullName;
    }

    private static string Notices() => File.ReadAllText(Path.Combine(Root(), "THIRD-PARTY-NOTICES.txt"));

    [Fact]
    public void Every_package_is_listed()
    {
        string csproj = File.ReadAllText(
            Path.Combine(Root(), "src", "PingWatcher.App", "PingWatcher.App.csproj"));

        string[] packages =
            [.. Regex.Matches(csproj, "<PackageReference\\s+Include=\"([^\"]+)\"")
                     .Select(m => m.Groups[1].Value)];

        Assert.NotEmpty(packages);

        string notices = Notices();

        foreach (string package in packages)
        {
            Assert.True(notices.Contains(package, StringComparison.OrdinalIgnoreCase),
                        $"{package} が THIRD-PARTY-NOTICES.txt に載っていない");
        }
    }

    [Fact]
    public void The_bundled_font_and_oui_table_are_listed()
    {
        // NuGet だけでなく、exe に埋め込んでいるものも対象になる
        string notices = Notices();

        Assert.Contains("Noto Sans JP", notices, StringComparison.Ordinal);
        Assert.Contains("SIL Open Font License", notices, StringComparison.Ordinal);
        Assert.Contains("IEEE", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void The_license_texts_are_included_in_full()
    {
        // 「MIT です」と書くだけでは足りない。本文の添付が条件になっている
        string notices = Notices();

        Assert.Contains("Permission is hereby granted, free of charge", notices, StringComparison.Ordinal);
        Assert.Contains("Apache License", notices, StringComparison.Ordinal);
        Assert.Contains("SIL OPEN FONT LICENSE", notices, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_notices_are_embedded_in_the_executable()
    {
        // 配布物からテキストだけ消されても読めるように、exe にも埋め込んである。
        // csproj の指定が消えていないことを確かめる
        string csproj = File.ReadAllText(
            Path.Combine(Root(), "src", "PingWatcher.App", "PingWatcher.App.csproj"));

        Assert.Contains("LogicalName=\"THIRD-PARTY-NOTICES.txt\"", csproj, StringComparison.Ordinal);
    }
}
