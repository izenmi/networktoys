namespace PastelNet.Core.Models;

/// <summary>
/// 1 回分の測定結果。
///
/// 数百宛先 × 数百サンプルを保持するので、クラスではなく構造体にして GC 圧を避ける。
/// このサイズ（16 バイト）なら 500 宛先 × 300 サンプルで 2.4MB に収まる。
/// </summary>
/// <param name="TimestampTicks">測定時刻（<see cref="DateTime.Ticks"/>）。</param>
/// <param name="RttMs">往復時間（ミリ秒）。応答が無い場合は 0。</param>
/// <param name="Status">測定結果。</param>
public readonly record struct ProbeSample(long TimestampTicks, float RttMs, ProbeStatus Status)
{
    public static ProbeSample Success(long timestampTicks, double rttMs)
        => new(timestampTicks, (float)rttMs, ProbeStatus.Success);

    public static ProbeSample Failure(long timestampTicks, ProbeStatus status)
        => new(timestampTicks, 0f, status);

    public DateTime Timestamp => new(TimestampTicks, DateTimeKind.Local);
}
