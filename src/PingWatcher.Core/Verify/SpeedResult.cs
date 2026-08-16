using System.Globalization;
using PingWatcher.Core.Net;

namespace PingWatcher.Core.Verify;

/// <summary>速度を測った結果。</summary>
/// <param name="Bytes">実際に流したバイト数。</param>
/// <param name="ElapsedMs">掛かった時間。</param>
/// <param name="Problem">測れなかった理由。測れたなら null。</param>
public sealed record SpeedSample(long Bytes, double ElapsedMs, string? Problem = null)
{
    /// <summary>1 秒あたりのバイト数。時間が 0 なら 0（0 除算を作らない）。</summary>
    public double BytesPerSecond => ElapsedMs > 0 ? Bytes * 1000.0 / ElapsedMs : 0;
}

/// <summary>
/// 速度の合否を決める。
///
/// <b>目安を下回っても「繋がらない」わけではない</b>ので、不合格ではなく注意にする。
/// 遅いことは事実として残しつつ、疎通の可否とは分けて読めるようにするため。
///
/// 目安は「期待」欄に <b>MB/s</b> で書く。空なら測るだけで合格にする
/// （まず数字を並べて比べたい、という使い方が現場では多い）。
/// </summary>
public static class SpeedVerdict
{
    /// <summary>「期待」欄の目安を読む。数字として読めなければ null（＝目安なし）。</summary>
    public static double? ParseExpected(string? expect)
    {
        string text = (expect ?? "").Trim();
        if (text.Length == 0) return null;

        // 「20MB/s」「20 MB」のように単位が付いていても読めるようにする
        int end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.')) end++;

        return double.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && value > 0
                ? value
                : null;
    }

    public static CheckResult Judge(CheckItem item, string proxyName, SpeedSample sample)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sample);

        if (sample.Problem is { } problem)
            return new CheckResult(item.Name, item.Kind, item.Target, proxyName,
                                   CheckVerdict.Fail, problem, sample.ElapsedMs);

        if (sample.Bytes == 0)
            return new CheckResult(item.Name, item.Kind, item.Target, proxyName,
                                   CheckVerdict.Fail, "1 バイトも流れませんでした", sample.ElapsedMs);

        string measured = Describe(sample);
        double? expected = ParseExpected(item.Expect);

        if (expected is not { } target)
            return new CheckResult(item.Name, item.Kind, item.Target, proxyName,
                                   CheckVerdict.Pass, measured, sample.ElapsedMs);

        // 目安は MB/s（1024 基数。表示に使う ByteRateFormat と揃える）
        double actualMbps = sample.BytesPerSecond / (1024 * 1024);

        return actualMbps >= target
            ? new CheckResult(item.Name, item.Kind, item.Target, proxyName,
                              CheckVerdict.Pass, $"{measured}（目安 {Trim(target)} MB/s 以上）", sample.ElapsedMs)
            : new CheckResult(item.Name, item.Kind, item.Target, proxyName,
                              CheckVerdict.Warn,
                              $"{measured}　目安 {Trim(target)} MB/s を下回っています", sample.ElapsedMs);
    }

    /// <summary>測った値そのもの。合否に関わらず証跡には必ず残す。</summary>
    public static string Describe(SpeedSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        return $"{ByteRateFormat.Format(sample.BytesPerSecond)}"
             + $"（{FormatSize(sample.Bytes)} / {sample.ElapsedMs / 1000:0.0} 秒）";
    }

    private static string FormatSize(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);

        return mb >= 1
            ? mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB"
            : (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
    }

    private static string Trim(double value)
        => value.ToString("0.#", CultureInfo.InvariantCulture);
}
