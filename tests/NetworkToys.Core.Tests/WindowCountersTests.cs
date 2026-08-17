using NetworkToys.Core.Models;
using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

public class WindowCountersTests
{
    private static ProbeSample Ok(double rtt = 10) => ProbeSample.Success(0, rtt);
    private static ProbeSample Lost() => ProbeSample.Failure(0, ProbeStatus.TimedOut);

    [Fact]
    public void An_untouched_window_has_nothing()
    {
        var counters = new WindowCounters();

        Assert.False(counters.HasData);
        Assert.Equal(0, counters.LossPercent);
        Assert.Equal(0, counters.AverageMs);
    }

    [Fact]
    public void It_counts_attempts_and_successes()
    {
        var counters = new WindowCounters();
        counters.Start(0);

        counters.Add(Ok());
        counters.Add(Lost());
        counters.Add(Ok());
        counters.Add(Ok());

        Assert.Equal(4, counters.Attempts);
        Assert.Equal(3, counters.Successes);
        Assert.Equal(25, counters.LossPercent);
    }

    [Fact]
    public void Restarting_forgets_what_came_before()
    {
        // これがこの型の存在理由。作業前のサンプルを作業後の統計に混ぜない
        var counters = new WindowCounters();
        counters.Start(0);

        for (int i = 0; i < 100; i++)
            counters.Add(Ok(1));

        counters.Start(1000);

        counters.Add(Lost());
        counters.Add(Lost());
        counters.Add(Ok(50));

        Assert.Equal(3, counters.Attempts);
        Assert.Equal(1, counters.Successes);
        Assert.Equal(50, counters.AverageMs);

        // 100 件の良好なサンプルが残っていたら 2% 程度に薄まっていた
        Assert.True(counters.LossPercent > 60);
    }

    [Fact]
    public void Rtt_statistics_use_only_successful_samples()
    {
        var counters = new WindowCounters();
        counters.Start(0);

        counters.Add(Ok(10));
        counters.Add(Lost());
        counters.Add(Ok(30));

        Assert.Equal(20, counters.AverageMs);
        Assert.Equal(10, counters.MinMs);
        Assert.Equal(30, counters.MaxMs);
    }

    [Fact]
    public void Pending_samples_are_not_counted()
    {
        var counters = new WindowCounters();
        counters.Start(0);

        counters.Add(ProbeSample.Failure(0, ProbeStatus.Pending));

        Assert.False(counters.HasData);
    }

    [Fact]
    public void Dns_failures_do_not_inflate_the_loss_rate()
    {
        // 名前が引けないのは「測って届かなかった」のとは違う
        var counters = new WindowCounters();
        counters.Start(0);

        counters.Add(ProbeSample.Failure(0, ProbeStatus.DnsFailure));
        counters.Add(Ok());

        Assert.Equal(1, counters.Attempts);
        Assert.Equal(0, counters.LossPercent);
    }

    [Fact]
    public void A_refusal_counts_as_reached_but_not_as_a_response_time()
    {
        var counters = new WindowCounters();
        counters.Start(0);

        counters.Add(new ProbeSample(0, 5, ProbeStatus.Refused));

        Assert.Equal(1, counters.Attempts);
        Assert.Equal(0, counters.Successes);
        Assert.Equal(0, counters.MaxConsecutiveFailures);   // 相手は生きている
    }

    [Fact]
    public void The_longest_streak_of_failures_is_kept()
    {
        var counters = new WindowCounters();
        counters.Start(0);

        counters.Add(Ok());
        counters.Add(Lost());
        counters.Add(Lost());
        counters.Add(Lost());
        counters.Add(Ok());
        counters.Add(Lost());

        Assert.Equal(3, counters.MaxConsecutiveFailures);
    }

    [Fact]
    public void The_start_time_is_remembered()
    {
        var counters = new WindowCounters();
        counters.Start(12345);

        Assert.Equal(12345, counters.StartedAtTicks);
    }

    [Fact]
    public void A_refusal_is_a_response_not_loss()
    {
        // ポートが閉じているだけの相手が「連続失敗 0 なのにロス率 100%」に
        // ならないこと。拒否は応答が返っている
        var counters = new WindowCounters();
        counters.Start(0);

        counters.Add(new ProbeSample(0, 3f, ProbeStatus.Refused));
        counters.Add(new ProbeSample(0, 3f, ProbeStatus.Refused));

        Assert.Equal(2, counters.Attempts);
        Assert.Equal(0, counters.LossPercent);
        Assert.Equal(0, counters.MaxConsecutiveFailures);
    }
}
