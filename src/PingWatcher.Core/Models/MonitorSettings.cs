namespace PingWatcher.Core.Models;

/// <summary>測定全体の既定値。宛先ごとの設定が無ければこれが使われる。</summary>
public sealed class MonitorSettings
{
    /// <summary>測定間隔（ミリ秒）。</summary>
    public int IntervalMs { get; set; } = 1000;

    /// <summary>応答待ちの上限（ミリ秒）。</summary>
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>
    /// 同時に飛ばすプローブ数の上限。宛先ごとに独立して回すので、
    /// これは「瞬間的に空を飛んでいるパケット数」の上限にあたる。
    /// </summary>
    public int MaxConcurrency { get; set; } = 64;

    /// <summary>宛先ごとに保持する履歴の件数。</summary>
    public int HistoryLength { get; set; } = 300;

    /// <summary>この RTT を超えたら「遅延」として色を変える（ミリ秒）。</summary>
    public int SlowThresholdMs { get; set; } = 100;

    /// <summary>ICMP のペイロードサイズ（バイト）。</summary>
    public int PayloadBytes { get; set; } = 32;

    public MonitorSettings Clone() => new()
    {
        IntervalMs = IntervalMs,
        TimeoutMs = TimeoutMs,
        MaxConcurrency = MaxConcurrency,
        HistoryLength = HistoryLength,
        SlowThresholdMs = SlowThresholdMs,
        PayloadBytes = PayloadBytes,
    };

    /// <summary>読み込んだ値が壊れていても動くように、常識的な範囲へ収める。</summary>
    public void Normalize()
    {
        IntervalMs = Math.Clamp(IntervalMs, 100, 3_600_000);
        TimeoutMs = Math.Clamp(TimeoutMs, 100, 60_000);
        MaxConcurrency = Math.Clamp(MaxConcurrency, 1, 1024);
        HistoryLength = Math.Clamp(HistoryLength, 30, 10_000);
        SlowThresholdMs = Math.Clamp(SlowThresholdMs, 1, 60_000);
        PayloadBytes = Math.Clamp(PayloadBytes, 0, 65_500);
    }
}
