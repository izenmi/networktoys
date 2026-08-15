using System.Globalization;

namespace PingWatcher.Core.Net;

/// <summary>
/// 転送レートの人間可読整形（1024 基数）。"873 B/s" / "12.3 KB/s" / "4.6 MB/s"。
/// </summary>
public static class ByteRateFormat
{
    private static readonly string[] Units = ["KB/s", "MB/s", "GB/s", "TB/s"];

    public static string Format(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 0)
            bytesPerSecond = 0;

        if (bytesPerSecond < 1024)
            return bytesPerSecond.ToString("F0", CultureInfo.InvariantCulture) + " B/s";

        double value = bytesPerSecond;
        string unit = Units[^1];

        foreach (string candidate in Units)
        {
            value /= 1024;
            if (value < 1024 || candidate == Units[^1])
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
}
