using PastelNet.Core.Metrics;
using PastelNet.Core.Models;
using Xunit;

namespace PastelNet.Core.Tests;

public class RttStatisticsTests
{
    private static ProbeSample Ok(double rtt) => ProbeSample.Success(0, rtt);
    private static ProbeSample Lost() => ProbeSample.Failure(0, ProbeStatus.TimedOut);

    [Fact]
    public void Empty_input_yields_empty_statistics()
    {
        RttStatistics stats = RttStatistics.Compute([]);

        Assert.Equal(0, stats.Attempts);
        Assert.Equal(0, stats.Successes);
        Assert.Equal(0, stats.LossPercent);
        Assert.Equal(0, stats.AverageMs);
    }

    [Fact]
    public void Computes_min_average_and_max()
    {
        ProbeSample[] samples = [Ok(10), Ok(20), Ok(30)];
        RttStatistics stats = RttStatistics.Compute(samples);

        Assert.Equal(3, stats.Attempts);
        Assert.Equal(3, stats.Successes);
        Assert.Equal(0, stats.LossPercent);
        Assert.Equal(10, stats.MinMs);
        Assert.Equal(20, stats.AverageMs);
        Assert.Equal(30, stats.MaxMs);
    }

    [Fact]
    public void Loss_percent_uses_attempts_as_denominator()
    {
        ProbeSample[] samples = [Ok(10), Lost(), Ok(20), Lost()];
        RttStatistics stats = RttStatistics.Compute(samples);

        Assert.Equal(4, stats.Attempts);
        Assert.Equal(2, stats.Successes);
        Assert.Equal(50, stats.LossPercent);
        Assert.Equal(15, stats.AverageMs);   // 失敗分は平均に混ぜない
    }

    [Fact]
    public void All_lost_reports_full_loss_without_rtt()
    {
        ProbeSample[] samples = [Lost(), Lost(), Lost()];
        RttStatistics stats = RttStatistics.Compute(samples);

        Assert.Equal(100, stats.LossPercent);
        Assert.Equal(0, stats.MinMs);
        Assert.Equal(0, stats.AverageMs);
        Assert.Equal(0, stats.MaxMs);
    }

    [Fact]
    public void Pending_samples_are_not_counted_as_attempts()
    {
        ProbeSample[] samples = [ProbeSample.Failure(0, ProbeStatus.Pending), Ok(10)];
        RttStatistics stats = RttStatistics.Compute(samples);

        Assert.Equal(1, stats.Attempts);
        Assert.Equal(0, stats.LossPercent);
    }

    [Fact]
    public void Dns_failures_are_not_counted_as_attempts()
    {
        // 名前が引けないのは「測定して届かなかった」のとは違うので、ロス率を汚さない
        ProbeSample[] samples = [ProbeSample.Failure(0, ProbeStatus.DnsFailure), Ok(10)];
        RttStatistics stats = RttStatistics.Compute(samples);

        Assert.Equal(1, stats.Attempts);
        Assert.Equal(0, stats.LossPercent);
    }

    [Fact]
    public void Refused_counts_as_an_attempt_but_not_a_success()
    {
        ProbeSample[] samples = [ProbeSample.Failure(0, ProbeStatus.Refused), Ok(10)];
        RttStatistics stats = RttStatistics.Compute(samples);

        Assert.Equal(2, stats.Attempts);
        Assert.Equal(1, stats.Successes);
        Assert.Equal(50, stats.LossPercent);
    }

    [Fact]
    public void Steady_latency_has_no_jitter()
    {
        ProbeSample[] samples = [Ok(20), Ok(20), Ok(20), Ok(20)];
        Assert.Equal(0, RttStatistics.Compute(samples).JitterMs);
    }

    [Fact]
    public void Varying_latency_produces_jitter()
    {
        ProbeSample[] samples = [Ok(10), Ok(60), Ok(10), Ok(60)];
        Assert.True(RttStatistics.Compute(samples).JitterMs > 0);
    }

    [Fact]
    public void Jitter_does_not_bridge_a_gap_in_responses()
    {
        // ロスを挟んだ前後の RTT 差をジッタに数えない（連続していないため）
        ProbeSample[] withGap = [Ok(10), Lost(), Ok(200)];
        Assert.Equal(0, RttStatistics.Compute(withGap).JitterMs);
    }

    [Fact]
    public void P95_picks_the_nearest_rank()
    {
        // 1..100 の 95 パーセンタイルは 95
        var samples = new ProbeSample[100];
        for (int i = 0; i < 100; i++)
            samples[i] = Ok(i + 1);

        Assert.Equal(95, RttStatistics.Compute(samples).P95Ms);
    }

    [Fact]
    public void P95_of_a_single_sample_is_that_sample()
        => Assert.Equal(42, RttStatistics.Compute([Ok(42)]).P95Ms);

    [Fact]
    public void Statistics_come_from_the_ring_buffer_contents()
    {
        var buffer = new RingBuffer<ProbeSample>(3);
        buffer.Add(Ok(100));   // 押し出される
        buffer.Add(Ok(10));
        buffer.Add(Ok(20));
        buffer.Add(Ok(30));

        var samples = new ProbeSample[buffer.Count];
        buffer.CopyTo(samples);

        RttStatistics stats = RttStatistics.Compute(samples);
        Assert.Equal(20, stats.AverageMs);
        Assert.Equal(30, stats.MaxMs);
    }
}
