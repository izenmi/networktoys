using PastelNet.Core.Quality;
using Xunit;

namespace PastelNet.Core.Tests;

public class MosCalculatorTests
{
    [Fact]
    public void An_ideal_link_scores_near_the_maximum()
    {
        VoiceQuality quality = MosCalculator.Estimate(averageRttMs: 1, jitterMs: 0, lossPercent: 0);

        Assert.True(quality.Mos > 4.3, $"MOS が低すぎます: {quality.Mos}");
        Assert.True(quality.RFactor > 90);
    }

    [Fact]
    public void Heavy_loss_destroys_the_score()
    {
        VoiceQuality quality = MosCalculator.Estimate(20, 5, 40);

        Assert.True(quality.Mos < 2.0, $"MOS が高すぎます: {quality.Mos}");
    }

    [Fact]
    public void Total_loss_bottoms_out_at_one()
    {
        VoiceQuality quality = MosCalculator.Estimate(500, 100, 100);

        Assert.Equal(1.0, quality.Mos, 3);
        Assert.Equal(0, quality.RFactor);
    }

    [Fact]
    public void More_loss_never_improves_the_score()
    {
        double previous = double.MaxValue;

        for (int loss = 0; loss <= 50; loss += 5)
        {
            double mos = MosCalculator.Estimate(30, 5, loss).Mos;
            Assert.True(mos <= previous, $"損失 {loss}% で MOS が上がりました");
            previous = mos;
        }
    }

    [Fact]
    public void More_latency_never_improves_the_score()
    {
        double previous = double.MaxValue;

        for (int rtt = 0; rtt <= 600; rtt += 25)
        {
            double mos = MosCalculator.Estimate(rtt, 0, 0).Mos;
            Assert.True(mos <= previous, $"RTT {rtt}ms で MOS が上がりました");
            previous = mos;
        }
    }

    [Fact]
    public void Jitter_weighs_more_than_plain_latency()
    {
        // ジッタは 2 倍で効くので、同じ増分なら遅延より悪化する
        double withLatency = MosCalculator.Estimate(50, 0, 0).Mos;
        double withJitter = MosCalculator.Estimate(30, 20, 0).Mos;

        Assert.True(withJitter < withLatency);
    }

    [Fact]
    public void The_score_stays_inside_its_range()
    {
        foreach (double rtt in (double[])[0, 10, 100, 1000, 10000])
        {
            foreach (double loss in (double[])[0, 1, 50, 100])
            {
                VoiceQuality quality = MosCalculator.Estimate(rtt, 0, loss);

                Assert.InRange(quality.Mos, 1.0, 4.5);
                Assert.InRange(quality.RFactor, 0, 100);
            }
        }
    }

    [Theory]
    [InlineData(4.4, "非常に良い")]
    [InlineData(4.1, "良い")]
    [InlineData(3.7, "普通")]
    [InlineData(3.2, "悪い")]
    [InlineData(2.0, "通話に耐えない")]
    public void Grades_map_to_plain_words(double mos, string expected)
        => Assert.Equal(expected, MosCalculator.GradeOf(mos));
}
