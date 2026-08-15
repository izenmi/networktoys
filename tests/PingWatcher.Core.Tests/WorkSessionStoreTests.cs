using PingWatcher.Core.Work;
using Xunit;

namespace PingWatcher.Core.Tests;

public class WorkSessionStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"pingwatcher-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void A_session_survives_a_round_trip()
    {
        string path = TempPath();

        try
        {
            var session = new WorkSession
            {
                Name = "配線替え（3階 EPS）",
                Before = new WorkSnapshot(
                    new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.FromHours(9)),
                    "作業前メモ",
                    1000,
                    [new WorkEntry("192.168.1.1|Icmp|0", "192.168.1.1", "ICMP", "192.168.1.1", "GW", true, 60, 0, 2, 3, 0)]),
                Markers = [new WorkMarker(DateTimeOffset.Now, "ケーブル差し替え")],
            };

            WorkSessionStore.Save(path, session);
            WorkSession? loaded = WorkSessionStore.Load(path, out string? error);

            Assert.Null(error);
            Assert.NotNull(loaded);
            Assert.Equal("配線替え（3階 EPS）", loaded.Name);
            Assert.Equal("GW", Assert.Single(loaded.Before!.Entries).Comment);
            Assert.Equal("ケーブル差し替え", Assert.Single(loaded.Markers).Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_broken_file_reports_an_error_instead_of_throwing()
    {
        string path = TempPath();

        try
        {
            File.WriteAllText(path, "{ これは JSON ではない");

            WorkSession? loaded = WorkSessionStore.Load(path, out string? error);

            Assert.Null(loaded);
            Assert.NotNull(error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void File_names_are_sortable_and_safe()
    {
        var startedAt = new DateTimeOffset(2026, 8, 15, 9, 30, 0, TimeSpan.FromHours(9));

        // 日時が先頭に来て Ordinal の並べ替えで新旧を追える
        Assert.StartsWith("20260815-0930", WorkSessionStore.BuildFileName(startedAt, "作業"), StringComparison.Ordinal);

        // ファイル名に使えない文字は置き換える
        string risky = WorkSessionStore.BuildFileName(startedAt, "a/b\\c:d");
        Assert.DoesNotContain('/', risky);
        Assert.DoesNotContain('\\', risky);
        Assert.DoesNotContain(':', risky);

        // 空の名前でも成立する
        Assert.Equal("20260815-0930.json", WorkSessionStore.BuildFileName(startedAt, ""));
    }
}
