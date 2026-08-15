using PingWatcher.Core.Models;
using PingWatcher.Core.Work;
using Xunit;

namespace PingWatcher.Core.Tests;

public class OutageTrackerTests
{
    private const string Key = "192.168.1.10|Icmp|0";
    private const string Host = "192.168.1.10";

    /// <summary>1 秒間隔で並ぶサンプル列を作るための時刻。</summary>
    private static long At(int second) => TimeSpan.FromSeconds(second).Ticks;

    private static ProbeSample Ok(int second) => ProbeSample.Success(At(second), 5);
    private static ProbeSample Lost(int second) => ProbeSample.Failure(At(second), ProbeStatus.TimedOut);

    [Fact]
    public void Nothing_is_recorded_while_everything_responds()
    {
        var tracker = new OutageTracker(1000);

        for (int i = 0; i < 10; i++)
            Assert.Null(tracker.Observe(Key, Host, Ok(i)));

        Assert.Empty(tracker.Records);
    }

    [Fact]
    public void A_single_drop_is_not_an_outage()
    {
        // 1 回きりの取りこぼしを不通として並べると、本命の切替が埋もれる
        var tracker = new OutageTracker(1000);

        tracker.Observe(Key, Host, Ok(0));
        tracker.Observe(Key, Host, Lost(1));
        tracker.Observe(Key, Host, Ok(2));

        Assert.Empty(tracker.Records);
        Assert.Equal(1, tracker.StateOf(Key)!.SingleDropCount);
    }

    [Fact]
    public void Two_failures_open_an_outage()
    {
        var tracker = new OutageTracker(1000);

        tracker.Observe(Key, Host, Ok(0));
        tracker.Observe(Key, Host, Lost(1));
        OutageRecord? opened = tracker.Observe(Key, Host, Lost(2));

        Assert.NotNull(opened);
        Assert.True(opened.IsOngoing);
        Assert.Equal(OutageCloseReason.Ongoing, opened.CloseReason);
    }

    [Fact]
    public void The_outage_starts_from_the_last_response_not_from_the_detection()
    {
        // 検知は 2 回目の失敗だが、実際に切れたのは最後に応答した直後
        var tracker = new OutageTracker(1000);

        tracker.Observe(Key, Host, Ok(10));
        tracker.Observe(Key, Host, Lost(11));
        tracker.Observe(Key, Host, Lost(12));
        OutageRecord? closed = tracker.Observe(Key, Host, Ok(14));

        Assert.NotNull(closed);
        Assert.Equal(At(10), closed.StartedAtTicks);
        Assert.Equal(At(14), closed.EndedAtTicks);
        Assert.Equal(TimeSpan.FromSeconds(4), closed.Duration);
    }

    [Fact]
    public void Recovery_closes_the_record()
    {
        var tracker = new OutageTracker(1000);

        tracker.Observe(Key, Host, Ok(0));
        tracker.Observe(Key, Host, Lost(1));
        tracker.Observe(Key, Host, Lost(2));
        OutageRecord? closed = tracker.Observe(Key, Host, Ok(3));

        Assert.NotNull(closed);
        Assert.False(closed.IsOngoing);
        Assert.Equal(OutageCloseReason.Recovered, closed.CloseReason);
        Assert.Single(tracker.Records);
        Assert.False(tracker.Records[0].IsOngoing);
    }

    [Fact]
    public void The_duration_is_shown_with_its_margin()
    {
        var tracker = new OutageTracker(1000);

        tracker.Observe(Key, Host, Ok(0));
        tracker.Observe(Key, Host, Lost(1));
        tracker.Observe(Key, Host, Lost(2));
        OutageRecord? closed = tracker.Observe(Key, Host, Ok(4));

        // 観測できる精度は測定間隔まで。裸の数字を出すと後から過信する
        Assert.Contains("約 4", closed!.DurationText, StringComparison.Ordinal);
        Assert.Contains("±1", closed.DurationText, StringComparison.Ordinal);
    }

    [Fact]
    public void An_outage_that_never_had_a_response_is_marked_unknown()
    {
        var tracker = new OutageTracker(1000);

        tracker.Observe(Key, Host, Lost(0));
        OutageRecord? opened = tracker.Observe(Key, Host, Lost(1));

        Assert.NotNull(opened);
        Assert.True(opened.StartUnknown);
    }

    [Fact]
    public void Dns_failures_are_distinguishable_from_a_real_outage()
    {
        // DNS サーバを触る作業では、ホスト名の宛先が一斉にこうなる。疎通断ではない
        var tracker = new OutageTracker(1000);

        tracker.Observe(Key, Host, Ok(0));
        tracker.Observe(Key, Host, ProbeSample.Failure(At(1), ProbeStatus.DnsFailure));
        OutageRecord? opened = tracker.Observe(Key, Host, ProbeSample.Failure(At(2), ProbeStatus.DnsFailure));

        Assert.Equal(ProbeStatus.DnsFailure, opened!.DominantStatus);
    }

    [Fact]
    public void Stopping_closes_open_records_without_claiming_recovery()
    {
        var tracker = new OutageTracker(1000);

        tracker.Observe(Key, Host, Ok(0));
        tracker.Observe(Key, Host, Lost(1));
        tracker.Observe(Key, Host, Lost(2));

        IReadOnlyList<OutageRecord> closed = tracker.CloseAll(At(5), OutageCloseReason.Stopped);

        Assert.Single(closed);
        Assert.Equal(OutageCloseReason.Stopped, closed[0].CloseReason);
        Assert.Equal(OutageCloseReason.Stopped, tracker.Records[0].CloseReason);
    }

    [Fact]
    public void Removing_a_target_closes_its_record_as_removed()
    {
        var tracker = new OutageTracker(1000);

        tracker.Observe(Key, Host, Ok(0));
        tracker.Observe(Key, Host, Lost(1));
        tracker.Observe(Key, Host, Lost(2));

        OutageRecord? closed = tracker.Remove(Key, At(3));

        Assert.NotNull(closed);
        Assert.Equal(OutageCloseReason.Removed, closed.CloseReason);
        Assert.Null(tracker.StateOf(Key));
    }

    [Fact]
    public void Targets_are_tracked_independently()
    {
        var tracker = new OutageTracker(1000);
        const string other = "192.168.1.20|Icmp|0";

        tracker.Observe(Key, Host, Ok(0));
        tracker.Observe(other, "192.168.1.20", Ok(0));

        tracker.Observe(Key, Host, Lost(1));
        tracker.Observe(Key, Host, Lost(2));
        tracker.Observe(other, "192.168.1.20", Ok(2));

        Assert.Single(tracker.Records);
        Assert.Equal(1, tracker.OngoingCount);
    }

    [Fact]
    public void Repeated_outages_are_all_kept()
    {
        var tracker = new OutageTracker(1000);
        int second = 0;

        for (int round = 0; round < 3; round++)
        {
            tracker.Observe(Key, Host, Ok(second++));
            tracker.Observe(Key, Host, Lost(second++));
            tracker.Observe(Key, Host, Lost(second++));
            tracker.Observe(Key, Host, Ok(second++));
        }

        Assert.Equal(3, tracker.Records.Count);
        Assert.All(tracker.Records, r => Assert.Equal(OutageCloseReason.Recovered, r.CloseReason));
    }

    [Fact]
    public void Older_records_are_dropped_beyond_the_limit()
    {
        var tracker = new OutageTracker(1000, maxRecords: 2);
        int second = 0;

        for (int round = 0; round < 5; round++)
        {
            tracker.Observe(Key, Host, Ok(second++));
            tracker.Observe(Key, Host, Lost(second++));
            tracker.Observe(Key, Host, Lost(second++));
            tracker.Observe(Key, Host, Ok(second++));
        }

        Assert.Equal(2, tracker.Records.Count);
        Assert.Equal(3, tracker.DroppedRecords);
    }

    [Fact]
    public void Outages_inside_a_period_can_be_looked_up()
    {
        // 「作業前後とも正常だが、途中で切れた」を拾うための問い合わせ
        var tracker = new OutageTracker(1000);

        tracker.Observe(Key, Host, Ok(10));
        tracker.Observe(Key, Host, Lost(11));
        tracker.Observe(Key, Host, Lost(12));
        tracker.Observe(Key, Host, Ok(13));

        Assert.Contains(Key, tracker.KeysWithOutageBetween(At(0), At(20)));
        Assert.Contains(Key, tracker.KeysWithOutageBetween(At(12), At(20)));
        Assert.DoesNotContain(Key, tracker.KeysWithOutageBetween(At(14), At(20)));
    }

    [Fact]
    public void An_ongoing_outage_counts_for_the_period()
    {
        var tracker = new OutageTracker(1000);

        tracker.Observe(Key, Host, Ok(10));
        tracker.Observe(Key, Host, Lost(11));
        tracker.Observe(Key, Host, Lost(12));

        Assert.Contains(Key, tracker.KeysWithOutageBetween(At(12), At(30)));
    }
}
