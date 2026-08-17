using System.Globalization;
using NetworkToys.Core.Models;

namespace NetworkToys.Core.Work;

/// <summary>不通が終わった理由。復旧と、こちらの都合で記録を閉じた場合を混ぜない。</summary>
public enum OutageCloseReason
{
    /// <summary>まだ続いている。</summary>
    Ongoing,

    /// <summary>応答が戻った。</summary>
    Recovered,

    /// <summary>測定を止めた。復旧したかどうかは分からない。</summary>
    Stopped,

    /// <summary>宛先が一覧から消えた。</summary>
    Removed,
}

/// <summary>
/// 1 回分の不通。
/// </summary>
/// <param name="TargetKey">対象（Host|Kind|Port）。</param>
/// <param name="Host">表示用のホスト名。</param>
/// <param name="StartedAtTicks">最後に応答があった時刻。ここから数える。</param>
/// <param name="EndedAtTicks">応答が戻った時刻。続いていれば null。</param>
/// <param name="MissedProbes">応答が無かった回数。</param>
/// <param name="IntervalMs">そのときの測定間隔。誤差の幅を示すのに要る。</param>
/// <param name="CloseReason">終わり方。</param>
/// <param name="DominantStatus">
/// 最も多かった失敗の種類。<b>DNS サーバを触る作業では、ホスト名の宛先が一斉に
/// DnsFailure になる</b>が、これは疎通が切れたのとは違う。区別できるように持つ。
/// </param>
/// <param name="StartUnknown">
/// 一度も応答が無いまま落ちていた場合。開始時刻は測定を始めた時刻で代用している。
/// </param>
public sealed record OutageRecord(
    string TargetKey,
    string Host,
    long StartedAtTicks,
    long? EndedAtTicks,
    int MissedProbes,
    int IntervalMs,
    OutageCloseReason CloseReason,
    ProbeStatus DominantStatus,
    bool StartUnknown)
{
    public DateTime StartedAt => new(StartedAtTicks, DateTimeKind.Local);

    public DateTime? EndedAt => EndedAtTicks is { } ticks ? new DateTime(ticks, DateTimeKind.Local) : null;

    public bool IsOngoing => EndedAtTicks is null;

    public TimeSpan? Duration => EndedAtTicks is { } ended
        ? TimeSpan.FromTicks(Math.Max(0, ended - StartedAtTicks))
        : null;

    /// <summary>
    /// 「約4秒（±1秒）」のように幅をつけて示す。
    /// 観測できる精度は測定間隔までなので、裸の数字を出すと後から過信する。
    /// </summary>
    public string DurationText
    {
        get
        {
            if (Duration is not { } duration)
                return StartUnknown ? "継続中（開始時刻不明）" : "継続中";

            double seconds = duration.TotalSeconds;
            double margin = IntervalMs / 1000.0;

            // 小数点は必ず「.」で出す。この文字列は UI だけでなく CSV にも流れるので、
            // カンマ小数点のカルチャに引きずられると列がずれる
            string body = seconds < 10
                ? string.Create(CultureInfo.InvariantCulture, $"約 {seconds:0.0} 秒")
                : string.Create(CultureInfo.InvariantCulture, $"約 {seconds:0} 秒");

            return string.Create(CultureInfo.InvariantCulture, $"{body}（±{margin:0.#} 秒）");
        }
    }
}
