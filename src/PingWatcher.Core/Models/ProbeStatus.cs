namespace PingWatcher.Core.Models;

/// <summary>
/// 1 回の測定結果。UI の状態バッジと色はこの値から決まる。
/// </summary>
public enum ProbeStatus
{
    /// <summary>まだ測っていない。</summary>
    Pending = 0,

    /// <summary>応答あり。</summary>
    Success,

    /// <summary>時間内に応答が無かった。</summary>
    TimedOut,

    /// <summary>到達不能（ICMP の Destination Unreachable など）。</summary>
    Unreachable,

    /// <summary>
    /// TCP で RST が返った。ポートは閉じているがホストは生きているので、
    /// タイムアウトとは意味がまったく違う。現場の切り分けで効くため区別する。
    /// </summary>
    Refused,

    /// <summary>名前を解決できなかった。</summary>
    DnsFailure,

    /// <summary>その他の失敗。</summary>
    Error,
}

public static class ProbeStatusExtensions
{
    /// <summary>到達性の判定。RTT を統計に含めてよいかどうかにも使う。</summary>
    public static bool IsReachable(this ProbeStatus status) => status is ProbeStatus.Success;

    /// <summary>
    /// 「測定は行われたが応答が無かった」= ロス率の分母に含めるべき状態か。
    /// 未測定と名前解決失敗は測定自体が成立していないので分母に入れない。
    /// </summary>
    public static bool CountsAsAttempt(this ProbeStatus status)
        => status is ProbeStatus.Success or ProbeStatus.TimedOut or ProbeStatus.Unreachable or ProbeStatus.Refused;

    /// <summary>レポートに載せる日本語名。テキストと HTML で表記を揃える。</summary>
    public static string Describe(this ProbeStatus status) => status switch
    {
        ProbeStatus.TimedOut => "無応答",
        ProbeStatus.DnsFailure => "名前を引けない",
        ProbeStatus.Refused => "接続拒否",
        ProbeStatus.Unreachable => "到達不能",
        _ => status.ToString(),
    };
}
