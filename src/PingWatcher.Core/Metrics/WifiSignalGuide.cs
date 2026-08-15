namespace PingWatcher.Core.Metrics;

/// <summary>
/// 受信強度(RSSI)の読み方の目安。現場でよく使われる閾値
/// (-50 快適 / -60 安定 / -67 が通話・会議の実用下限 / -75 を切ると弱い)に合わせる。
/// </summary>
public static class WifiSignalGuide
{
    public static string Describe(int rssi) => rssi switch
    {
        >= -50 => "非常に良い",
        >= -60 => "良い",
        >= -67 => "実用圏",
        >= -75 => "弱い",
        _ => "不安定",
    };
}
