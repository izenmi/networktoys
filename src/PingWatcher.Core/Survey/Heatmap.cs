namespace PingWatcher.Core.Survey;

/// <summary>ヒートマップに描く値の選び方。</summary>
public enum HeatmapMode
{
    /// <summary>測定時に接続していた AP の受信強度。</summary>
    Connected,

    /// <summary>その場所で最も強かった AP の受信強度（どこかには繋がれる、の地図）。</summary>
    Strongest,

    /// <summary>指定した BSSID の受信強度（特定 AP のカバレッジ）。</summary>
    SingleBssid,
}

/// <summary>描画対象の指定。Bssid は <see cref="HeatmapMode.SingleBssid"/> のときだけ使う。</summary>
public readonly record struct HeatmapSource(HeatmapMode Mode, string? Bssid);

/// <summary>補間に渡す 1 点（0..1 の正規化座標と dBm）。</summary>
public readonly record struct SamplePoint(double X, double Y, double Rssi);

/// <summary>
/// サーベイのヒートマップ計算。すべて純関数で、色は持ち込まない
/// （色は App 側のパレットキー <c>Brush.Heatmap.1〜6</c> が持つ）。
/// </summary>
public static class Heatmap
{
    public const int BandCount = 6;

    // 弱い側から数えるバンド境界。-65 dBm あたりが VoIP/ローミングの定番の目安
    private static readonly double[] BandBounds = [-85, -75, -65, -55, -45];

    /// <summary>
    /// 1 測定点から表示値を選ぶ。対象の AP がその点で見えていなければ null
    /// （グリッドに入れない。ゼロ扱いにすると「見えない」が「弱い」に化ける）。
    /// </summary>
    public static double? SelectValue(
        IReadOnlyList<SurveyReading> readings, string? connectedBssid, HeatmapSource source)
    {
        switch (source.Mode)
        {
            case HeatmapMode.Connected:
                return string.IsNullOrEmpty(connectedBssid) ? null : FindRssi(readings, connectedBssid);

            case HeatmapMode.SingleBssid:
                return string.IsNullOrEmpty(source.Bssid) ? null : FindRssi(readings, source.Bssid);

            default:
                double? best = null;
                foreach (SurveyReading reading in readings)
                {
                    if (best is null || reading.Rssi > best)
                        best = reading.Rssi;
                }
                return best;
        }
    }

    /// <summary>
    /// 逆距離加重（IDW、べき 2）でグリッドへ補間する。戻り値は row-major の
    /// float[gridWidth * gridHeight]。影響半径（正規化座標）の中に測定点が
    /// 1 つも無いセルは NaN ＝未測定（塗らない。NetSpot と同じ扱い）。
    /// </summary>
    public static float[] Interpolate(
        ReadOnlySpan<SamplePoint> samples, int gridWidth, int gridHeight,
        double power = 2.0, double radius = 0.25)
    {
        var grid = new float[gridWidth * gridHeight];
        double radiusSquared = radius * radius;

        for (int gy = 0; gy < gridHeight; gy++)
        {
            double y = (gy + 0.5) / gridHeight;

            for (int gx = 0; gx < gridWidth; gx++)
            {
                double x = (gx + 0.5) / gridWidth;

                double weightSum = 0;
                double valueSum = 0;
                float cell = float.NaN;

                foreach (SamplePoint sample in samples)
                {
                    double dx = sample.X - x;
                    double dy = sample.Y - y;
                    double distanceSquared = dx * dx + dy * dy;

                    if (distanceSquared > radiusSquared)
                        continue;

                    // 測定点そのもの（0 除算回避を兼ねる）は実測値で確定
                    if (distanceSquared < 1e-12)
                    {
                        weightSum = 0;
                        cell = (float)sample.Rssi;
                        break;
                    }

                    double weight = 1.0 / Math.Pow(distanceSquared, power / 2.0);
                    weightSum += weight;
                    valueSum += weight * sample.Rssi;
                }

                if (weightSum > 0)
                    cell = (float)(valueSum / weightSum);

                grid[gy * gridWidth + gx] = cell;
            }
        }

        return grid;
    }

    /// <summary>dBm → バンド添字（0=最弱 .. 5=最強）。境界は -85/-75/-65/-55/-45。</summary>
    public static int BandIndex(double rssi)
    {
        int index = 0;
        foreach (double bound in BandBounds)
        {
            if (rssi >= bound)
                index++;
        }

        return index;
    }

    /// <summary>
    /// 枠の中にアスペクト比を保って内接させたときの矩形（レターボックス）。
    /// SurveyCanvas の描画・クリック座標変換・PNG 書き出しが同じ結果を共有する。
    /// </summary>
    public static (double X, double Y, double Width, double Height) FitRect(
        double aspectRatio, double boxWidth, double boxHeight)
    {
        if (aspectRatio <= 0 || boxWidth <= 0 || boxHeight <= 0)
            return (0, 0, 0, 0);

        double width = boxWidth;
        double height = width / aspectRatio;

        if (height > boxHeight)
        {
            height = boxHeight;
            width = height * aspectRatio;
        }

        return ((boxWidth - width) / 2, (boxHeight - height) / 2, width, height);
    }

    private static double? FindRssi(IReadOnlyList<SurveyReading> readings, string bssid)
    {
        foreach (SurveyReading reading in readings)
        {
            if (string.Equals(reading.Bssid, bssid, StringComparison.OrdinalIgnoreCase))
                return reading.Rssi;
        }

        return null;
    }
}
