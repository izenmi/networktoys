using System.Buffers;
using PastelNet.Core.Models;

namespace PastelNet.Core.Metrics;

/// <summary>
/// 測定履歴から求めた統計。すべて純粋な計算なのでユニットテストで押さえられる。
/// </summary>
/// <param name="Attempts">ロス率の分母になる試行回数。</param>
/// <param name="Successes">応答があった回数。</param>
/// <param name="LossPercent">ロス率（%）。試行が 0 なら 0。</param>
/// <param name="MinMs">最小 RTT。応答が無ければ 0。</param>
/// <param name="AverageMs">平均 RTT。</param>
/// <param name="MaxMs">最大 RTT。</param>
/// <param name="P95Ms">95 パーセンタイル RTT。</param>
/// <param name="JitterMs">ジッタ（RFC 3550 の平滑化方式）。</param>
public readonly record struct RttStatistics(
    int Attempts,
    int Successes,
    double LossPercent,
    double MinMs,
    double AverageMs,
    double MaxMs,
    double P95Ms,
    double JitterMs)
{
    public static readonly RttStatistics Empty = new(0, 0, 0, 0, 0, 0, 0, 0);

    public static RttStatistics Compute(ReadOnlySpan<ProbeSample> samples)
    {
        int attempts = 0;
        int successes = 0;
        double min = double.MaxValue;
        double max = 0;
        double sum = 0;

        // RFC 3550: J += (|D(i-1,i)| - J) / 16
        // 連続する成功サンプル間の RTT の変動を平滑化する。
        double jitter = 0;
        double previousRtt = double.NaN;

        foreach (ProbeSample sample in samples)
        {
            if (sample.Status.CountsAsAttempt())
                attempts++;

            if (!sample.Status.IsReachable())
            {
                // 応答が途切れたら差分の連続性も切れる
                previousRtt = double.NaN;
                continue;
            }

            successes++;
            double rtt = sample.RttMs;
            sum += rtt;
            if (rtt < min) min = rtt;
            if (rtt > max) max = rtt;

            if (!double.IsNaN(previousRtt))
                jitter += (Math.Abs(rtt - previousRtt) - jitter) / 16.0;

            previousRtt = rtt;
        }

        if (successes == 0)
        {
            double lossOnly = attempts == 0 ? 0 : 100.0;
            return new RttStatistics(attempts, 0, lossOnly, 0, 0, 0, 0, 0);
        }

        double loss = attempts == 0 ? 0 : (attempts - successes) * 100.0 / attempts;

        return new RttStatistics(
            attempts,
            successes,
            loss,
            min,
            sum / successes,
            max,
            ComputePercentile(samples, successes, 0.95),
            jitter);
    }

    /// <summary>nearest-rank 方式のパーセンタイル。</summary>
    private static double ComputePercentile(ReadOnlySpan<ProbeSample> samples, int successes, double percentile)
    {
        double[] rented = ArrayPool<double>.Shared.Rent(successes);
        try
        {
            int n = 0;
            foreach (ProbeSample sample in samples)
            {
                if (sample.Status.IsReachable())
                    rented[n++] = sample.RttMs;
            }

            Span<double> values = rented.AsSpan(0, n);
            values.Sort();

            int rank = (int)Math.Ceiling(percentile * n) - 1;
            return values[Math.Clamp(rank, 0, n - 1)];
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }
}
