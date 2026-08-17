using NetworkToys.Core.Models;
using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

public class ProbeStateMachineTests
{
    private static ProbeSample Ok(long ticks = 0) => ProbeSample.Success(ticks, 5);
    private static ProbeSample Lost(long ticks = 0) => ProbeSample.Failure(ticks, ProbeStatus.TimedOut);

    [Fact]
    public void Starts_pending()
    {
        var machine = new ProbeStateMachine();

        Assert.Equal(LinkState.Pending, machine.State);
        Assert.Null(machine.LastSuccessTicks);
    }

    [Fact]
    public void One_response_brings_it_up()
    {
        var machine = new ProbeStateMachine();
        machine.Observe(Ok(100));

        Assert.Equal(LinkState.Up, machine.State);
        Assert.Equal(100, machine.LastSuccessTicks);
    }

    [Fact]
    public void One_failure_is_not_enough_to_call_it_down()
    {
        var machine = new ProbeStateMachine();
        machine.Observe(Ok());
        machine.Observe(Lost());

        Assert.Equal(LinkState.Up, machine.State);
        Assert.Equal(1, machine.ConsecutiveFailures);
    }

    [Fact]
    public void Two_failures_in_a_row_declare_it_down()
    {
        var machine = new ProbeStateMachine();
        machine.Observe(Ok());
        machine.Observe(Lost());
        machine.Observe(Lost());

        Assert.Equal(LinkState.Down, machine.State);
    }

    [Fact]
    public void Every_sample_counts_even_when_they_arrive_together()
    {
        // 以前は「最新の1件」しか見ていなかったため、まとめて届いた失敗を数え落としていた。
        // ここが今回の作り直しの要点なので、仕様として固定する。
        var machine = new ProbeStateMachine();
        machine.Observe(Ok());

        foreach (int i in Enumerable.Range(0, 5))
            machine.Observe(Lost(i));

        Assert.Equal(5, machine.ConsecutiveFailures);
        Assert.Equal(LinkState.Down, machine.State);
    }

    [Fact]
    public void A_response_clears_the_streak()
    {
        var machine = new ProbeStateMachine();
        machine.Observe(Ok());
        machine.Observe(Lost());
        machine.Observe(Lost());
        machine.Observe(Ok(500));

        Assert.Equal(LinkState.Up, machine.State);
        Assert.Equal(0, machine.ConsecutiveFailures);
        Assert.Equal(500, machine.LastSuccessTicks);
    }

    [Fact]
    public void A_refusal_counts_as_a_response()
    {
        // TCP の RST は「相手は生きている」証拠なので、落ちたとは数えない
        var machine = new ProbeStateMachine();
        machine.Observe(Ok());
        machine.Observe(ProbeSample.Failure(10, ProbeStatus.Refused));
        machine.Observe(ProbeSample.Failure(20, ProbeStatus.Refused));

        Assert.Equal(LinkState.Up, machine.State);
        Assert.Equal(0, machine.ConsecutiveFailures);
    }

    [Fact]
    public void Single_drops_are_counted_separately()
    {
        var machine = new ProbeStateMachine();
        machine.Observe(Ok());
        machine.Observe(Lost());
        machine.Observe(Ok());   // 1 回だけ落ちて戻った
        machine.Observe(Lost());
        machine.Observe(Ok());

        Assert.Equal(2, machine.SingleDropCount);
        Assert.Equal(LinkState.Up, machine.State);
    }

    [Fact]
    public void A_real_outage_is_not_counted_as_a_single_drop()
    {
        var machine = new ProbeStateMachine();
        machine.Observe(Ok());
        machine.Observe(Lost());
        machine.Observe(Lost());
        machine.Observe(Lost());
        machine.Observe(Ok());

        Assert.Equal(0, machine.SingleDropCount);
        Assert.Equal(3, machine.MaxConsecutiveFailures);
    }

    [Fact]
    public void The_longest_streak_is_remembered()
    {
        var machine = new ProbeStateMachine();
        machine.Observe(Ok());
        machine.Observe(Lost());
        machine.Observe(Lost());
        machine.Observe(Ok());
        machine.Observe(Lost());
        machine.Observe(Lost());
        machine.Observe(Lost());
        machine.Observe(Lost());
        machine.Observe(Ok());

        Assert.Equal(4, machine.MaxConsecutiveFailures);
        Assert.Equal(0, machine.ConsecutiveFailures);
    }

    [Fact]
    public void Observe_reports_whether_the_state_moved()
    {
        var machine = new ProbeStateMachine();

        Assert.True(machine.Observe(Ok()));    // Pending -> Up
        Assert.False(machine.Observe(Ok()));   // Up のまま
        Assert.False(machine.Observe(Lost())); // まだ Up
        Assert.True(machine.Observe(Lost()));  // Up -> Down
        Assert.False(machine.Observe(Lost())); // Down のまま
        Assert.True(machine.Observe(Ok()));    // Down -> Up
    }

    [Fact]
    public void Silence_becomes_stalled()
    {
        // 「応答が無い」と「測っていない」は別物。混ぜると変更作業の確認にならない
        var machine = new ProbeStateMachine();
        long start = DateTime.Now.Ticks;
        machine.Observe(Ok(start));

        Assert.False(machine.CheckStalled(start + TimeSpan.FromSeconds(2).Ticks, 1000));
        Assert.Equal(LinkState.Up, machine.State);

        Assert.True(machine.CheckStalled(start + TimeSpan.FromSeconds(4).Ticks, 1000));
        Assert.Equal(LinkState.Stalled, machine.State);
    }

    [Fact]
    public void A_new_sample_brings_it_back_from_stalled()
    {
        var machine = new ProbeStateMachine();
        long start = DateTime.Now.Ticks;
        machine.Observe(Ok(start));
        machine.CheckStalled(start + TimeSpan.FromSeconds(10).Ticks, 1000);

        Assert.Equal(LinkState.Stalled, machine.State);

        machine.Observe(Ok(start + TimeSpan.FromSeconds(11).Ticks));
        Assert.Equal(LinkState.Up, machine.State);
    }

    [Fact]
    public void Nothing_is_stalled_before_the_first_sample()
    {
        var machine = new ProbeStateMachine();

        Assert.False(machine.CheckStalled(DateTime.Now.Ticks, 1000));
        Assert.Equal(LinkState.Pending, machine.State);
    }

    [Fact]
    public void Reset_clears_everything()
    {
        var machine = new ProbeStateMachine();
        machine.Observe(Ok());
        machine.Observe(Lost());
        machine.Reset();

        Assert.Equal(LinkState.Pending, machine.State);
        Assert.Equal(0, machine.ConsecutiveFailures);
        Assert.Equal(0, machine.MaxConsecutiveFailures);
        Assert.Null(machine.LastSuccessTicks);
    }

    [Fact]
    public void Stalling_remembers_the_reachability_before_the_stall()
    {
        // 測定が詰まって「停止」になっても、不達だった宛先を
        // 「応答あり」側へ振り分け直してはいけない（実際に起きた誤分類）
        var machine = new ProbeStateMachine();

        machine.Observe(ProbeSample.Failure(TimeSpan.FromSeconds(0).Ticks, ProbeStatus.TimedOut));
        machine.Observe(ProbeSample.Failure(TimeSpan.FromSeconds(1).Ticks, ProbeStatus.TimedOut));
        Assert.Equal(LinkState.Down, machine.State);

        Assert.True(machine.CheckStalled(TimeSpan.FromSeconds(30).Ticks, intervalMs: 1000));

        Assert.Equal(LinkState.Stalled, machine.State);
        Assert.Equal(LinkState.Down, machine.StateBeforeStall);
    }

    [Fact]
    public void A_pending_sample_does_not_feed_the_stall_timer()
    {
        // 「まだ測っていない」を観測扱いにすると、結果が途絶えた検知が空振りで延命される
        var machine = new ProbeStateMachine();

        machine.Observe(ProbeSample.Success(TimeSpan.FromSeconds(0).Ticks, 5));
        machine.Observe(new ProbeSample(TimeSpan.FromSeconds(29).Ticks, 0, ProbeStatus.Pending));

        Assert.True(machine.CheckStalled(TimeSpan.FromSeconds(30).Ticks, intervalMs: 1000));
        Assert.Equal(LinkState.Stalled, machine.State);
    }

    [Fact]
    public void Resuming_does_not_flag_the_gap_as_a_stall()
    {
        // 測定を止めて数秒後に再開すると、前回のサンプル時刻との差だけで
        // 「停止」になっていた。再開の合図で時計を合わせ直す
        var machine = new ProbeStateMachine();
        machine.Observe(ProbeSample.Success(TimeSpan.FromSeconds(0).Ticks, 5));

        // 30 秒後に再開
        machine.NotifyResumed(TimeSpan.FromSeconds(30).Ticks);

        Assert.False(machine.CheckStalled(TimeSpan.FromSeconds(31).Ticks, intervalMs: 1000));
        Assert.Equal(LinkState.Up, machine.State);
    }

    [Fact]
    public void Resuming_restores_the_state_before_the_stall()
    {
        var machine = new ProbeStateMachine();
        machine.Observe(ProbeSample.Failure(TimeSpan.FromSeconds(0).Ticks, ProbeStatus.TimedOut));
        machine.Observe(ProbeSample.Failure(TimeSpan.FromSeconds(1).Ticks, ProbeStatus.TimedOut));
        machine.CheckStalled(TimeSpan.FromSeconds(30).Ticks, intervalMs: 1000);
        Assert.Equal(LinkState.Stalled, machine.State);

        machine.NotifyResumed(TimeSpan.FromSeconds(60).Ticks);

        Assert.Equal(LinkState.Down, machine.State);
    }
}
