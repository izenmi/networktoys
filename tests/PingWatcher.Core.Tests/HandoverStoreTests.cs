using PingWatcher.Core.Models;
using PingWatcher.Core.Storage;
using PingWatcher.Core.Work;
using Xunit;

namespace PingWatcher.Core.Tests;

public class HandoverStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"pw-handover-{Guid.NewGuid():N}.json");

    [Fact]
    public void A_handover_round_trips_through_the_file()
    {
        string path = TempPath();

        var document = new HandoverDocument
        {
            WasRunning = true,
            SelectedTab = "Ping",
            WindowWidth = 1024,
            WindowHeight = 768,
            Targets =
            [
                new HandoverTarget
                {
                    Key = "192.168.1.1|ICMP|0",
                    Address = "192.168.1.1",
                    Ticks = [100, 200, 300],
                    Rtt = [1.5, 2.5, 0],
                    Status = [1, 1, 2],
                    Window = new HandoverWindow { Attempts = 3, Successes = 2, Responses = 2, SumMs = 4.0 },
                },
            ],
        };

        HandoverStore.Save(path, document);
        HandoverDocument? loaded = HandoverStore.LoadAndDelete(path);

        Assert.NotNull(loaded);
        Assert.True(loaded!.WasRunning);
        Assert.Equal("Ping", loaded.SelectedTab);
        Assert.Equal(1024, loaded.WindowWidth);

        HandoverTarget target = Assert.Single(loaded.Targets);
        Assert.Equal("192.168.1.1|ICMP|0", target.Key);
        Assert.Equal(new[] { 100L, 200L, 300L }, target.Ticks.ToArray());
        Assert.Equal(3, target.Window.Attempts);
    }

    [Fact]
    public void Reading_always_deletes_the_file()
    {
        string path = TempPath();
        HandoverStore.Save(path, new HandoverDocument());

        HandoverStore.LoadAndDelete(path);

        // 機器の出力(認証情報を含みうる)を置きっぱなしにしない
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_broken_file_is_discarded_and_deleted_instead_of_throwing()
    {
        string path = TempPath();
        File.WriteAllText(path, "これは JSON ではありません");

        Assert.Null(HandoverStore.LoadAndDelete(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_missing_file_is_simply_nothing()
        => Assert.Null(HandoverStore.LoadAndDelete(TempPath()));

    [Fact]
    public void An_unknown_version_is_ignored()
    {
        string path = TempPath();
        HandoverStore.Save(path, new HandoverDocument { Version = 99, SelectedTab = "Ping" });

        // 引き継げないより、当てにいって壊れる方が困る
        Assert.Null(HandoverStore.LoadAndDelete(path));
    }

    [Fact]
    public void The_pasted_device_output_survives_the_handover()
    {
        string path = TempPath();

        HandoverStore.Save(path, new HandoverDocument
        {
            Panels = new HandoverPanels
            {
                Devices =
                [
                    new HandoverDevice
                    {
                        Name = "core-sw",
                        Pasted = [new HandoverPaste { Kind = 2, Before = "before\nlines", After = "after\nlines" }],
                    },
                ],
                SelectedDevice = "core-sw",
            },
        });

        HandoverDocument? loaded = HandoverStore.LoadAndDelete(path);

        HandoverDevice device = Assert.Single(loaded!.Panels.Devices);
        Assert.Equal("core-sw", device.Name);
        Assert.Equal("before\nlines", Assert.Single(device.Pasted).Before);
    }

    // ===== 作業窓の書き戻し =====

    [Fact]
    public void Restored_window_counters_keep_totals_the_history_can_no_longer_prove()
    {
        // 窓を開いてから 1200 回測ったが、履歴のリングは 300 件しか持てない。
        // 履歴を再生して数え直すと試行回数が 300 に減り、作業前後の比較が静かにずれる
        WindowCounters restored = WindowCounters.Restore(
            startedAtTicks: 12345, attempts: 1200, successes: 1150, responses: 1160,
            sumMs: 2300, minMs: 1.0, maxMs: 40.0,
            maxConsecutiveFailures: 7, consecutiveFailures: 2);

        Assert.Equal(1200, restored.Attempts);
        Assert.Equal(12345, restored.StartedAtTicks);
        Assert.Equal(7, restored.MaxConsecutiveFailures);
        Assert.Equal(2, restored.ConsecutiveFailures);
        Assert.Equal(2.0, restored.AverageMs);
        Assert.Equal(40.0 / 12, restored.LossPercent, 3);
    }

    [Fact]
    public void A_restored_window_keeps_counting_from_where_it_left_off()
    {
        WindowCounters window = WindowCounters.Restore(
            startedAtTicks: 1, attempts: 10, successes: 10, responses: 10,
            sumMs: 100, minMs: 5, maxMs: 20,
            maxConsecutiveFailures: 3, consecutiveFailures: 2);

        window.Add(ProbeSample.Failure(2, ProbeStatus.TimedOut));

        // 連続失敗が 2 から続いているので、1 回足すと最長 3 に並ぶ（0 から数え直さない）
        Assert.Equal(11, window.Attempts);
        Assert.Equal(3, window.ConsecutiveFailures);
        Assert.Equal(3, window.MaxConsecutiveFailures);
    }
}
