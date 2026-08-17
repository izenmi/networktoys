using System.Globalization;

namespace NetworkToys.Core.Net;

/// <summary>
/// 回線速度の整形。<b>bps（ビット毎秒）で、1000 基数。</b>
///
/// 回線の速度は昔から bps で語られ、契約書も測定サイトもこの単位で書かれている。
/// 現場の人が見比べる相手がそちらなので、こちらも合わせる。
///
/// <b><see cref="ByteRateFormat"/> とは基数が違う。</b>あちらは転送量なので
/// バイト・1024 基数（接続タブの B/秒）。混ぜないこと。
/// </summary>
public static class BitRateFormat
{
    private static readonly string[] Units = ["kbps", "Mbps", "Gbps", "Tbps"];

    /// <param name="bytesPerSecond">測ったバイト毎秒。ここでビットに直す。</param>
    public static string Format(double bytesPerSecond)
    {
        double bits = double.IsNaN(bytesPerSecond) || bytesPerSecond < 0 ? 0 : bytesPerSecond * 8;

        if (bits < 1000)
            return bits.ToString("F0", CultureInfo.InvariantCulture) + " bps";

        double value = bits;
        string unit = Units[^1];

        foreach (string candidate in Units)
        {
            value /= 1000;
            if (value < 1000 || candidate == Units[^1])
            {
                unit = candidate;
                break;
            }
        }

        // 2 桁までは小数 1 桁、3 桁からは整数のみ（列幅を暴れさせない）
        string number = value < 100
            ? value.ToString("F1", CultureInfo.InvariantCulture)
            : value.ToString("F0", CultureInfo.InvariantCulture);

        return number + " " + unit;
    }

    /// <summary>バイト毎秒を Mbps の数値にする。目安との比較に使う。</summary>
    public static double ToMbps(double bytesPerSecond)
        => bytesPerSecond * 8 / 1_000_000;
}
