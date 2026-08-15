using PastelNet.Core.Models;

namespace PastelNet.Core.Work;

/// <summary>
/// ある時点から先だけを数える集計。
///
/// なぜ要るか: 履歴のリングバッファ全体から統計を出すと、<b>作業前の良好なサンプルが
/// 作業後の平均とロス率を薄める</b>。1 秒間隔なら作業後 5 分間は前のデータが混ざり続け、
/// 実際には 10% 落ちているのに 2% に見える。前後比較の判定が静かに壊れる。
///
/// リングバッファは古い分を捨てるので「マーク以降」を遡って数え直すこともできない。
/// だから通過した分をその場で足し込む。
/// </summary>
public struct WindowCounters
{
    private int _consecutiveFailures;

    /// <summary>数え始めた時刻（Ticks）。</summary>
    public long StartedAtTicks { get; private set; }

    /// <summary>ロス率の分母になる試行回数。</summary>
    public int Attempts { get; private set; }

    /// <summary>応答があった回数。</summary>
    public int Successes { get; private set; }

    /// <summary>応答があったサンプルの RTT 合計。</summary>
    public double SumMs { get; private set; }

    public double MinMs { get; private set; }

    public double MaxMs { get; private set; }

    /// <summary>この窓の中で最も長く続いた連続失敗。</summary>
    public int MaxConsecutiveFailures { get; private set; }

    public readonly bool HasData => Attempts > 0;

    public readonly double LossPercent => Attempts == 0 ? 0 : (Attempts - Successes) * 100.0 / Attempts;

    public readonly double AverageMs => Successes == 0 ? 0 : SumMs / Successes;

    /// <summary>数え始める。以後のサンプルだけが対象になる。</summary>
    public void Start(long startedAtTicks)
    {
        StartedAtTicks = startedAtTicks;
        Attempts = 0;
        Successes = 0;
        SumMs = 0;
        MinMs = 0;
        MaxMs = 0;
        MaxConsecutiveFailures = 0;
        _consecutiveFailures = 0;
    }

    public void Add(in ProbeSample sample)
    {
        if (sample.Status == ProbeStatus.Pending)
            return;

        if (sample.Status.CountsAsAttempt())
            Attempts++;

        // TCP の「拒否」は相手が生きている証拠なので、失敗の連続には数えない。
        // ただし RTT の統計には入れない（接続できたわけではないため）
        if (sample.Status is ProbeStatus.Success or ProbeStatus.Refused)
        {
            _consecutiveFailures = 0;
        }
        else
        {
            _consecutiveFailures++;
            if (_consecutiveFailures > MaxConsecutiveFailures)
                MaxConsecutiveFailures = _consecutiveFailures;
        }

        if (sample.Status != ProbeStatus.Success)
            return;

        Successes++;
        SumMs += sample.RttMs;

        if (Successes == 1 || sample.RttMs < MinMs)
            MinMs = sample.RttMs;

        if (sample.RttMs > MaxMs)
            MaxMs = sample.RttMs;
    }
}
