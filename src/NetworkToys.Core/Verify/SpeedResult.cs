using System.Globalization;
using NetworkToys.Core.Net;

namespace NetworkToys.Core.Verify;

/// <summary>速度を測った結果。</summary>
/// <param name="Bytes">実際に流したバイト数。</param>
/// <param name="ElapsedMs">掛かった時間。</param>
/// <param name="Problem">測れなかった理由。測れたなら null。</param>
public sealed record SpeedSample(long Bytes, double ElapsedMs, string? Problem = null)
{
    /// <summary>1 秒あたりのバイト数。時間が 0 なら 0（0 除算を作らない）。</summary>
    public double BytesPerSecond => ElapsedMs > 0 ? Bytes * 1000.0 / ElapsedMs : 0;

    /// <summary>測れたか。</summary>
    public bool Measured => Problem is null && Bytes > 0;
}

/// <summary>
/// 速度の合否を決める。
///
/// <b>単位は bps。</b>回線の速度は契約書も測定サイトも bps で書かれているので、
/// 現場の人が見比べる相手に合わせる。目安も「期待」欄に <b>Mbps</b> で書く。
///
/// <b>目安を下回っても「繋がらない」わけではない</b>ので、不合格ではなく注意にする。
/// 遅いことは事実として残しつつ、疎通の可否とは分けて読めるようにするため。
/// 期待欄が空なら測るだけで合格にする（まず数字を並べて比べたい、という使い方が多い）。
/// </summary>
public static class SpeedVerdict
{
    /// <summary>「期待」欄の目安（Mbps）を読む。数字として読めなければ null（＝目安なし）。</summary>
    public static double? ParseExpected(string? expect)
    {
        string text = (expect ?? "").Trim();
        if (text.Length == 0) return null;

        // 「20Mbps」「20 M」のように単位が付いていても読めるようにする
        int end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.')) end++;

        return double.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && value > 0
                ? value
                : null;
    }

    /// <param name="down">下りの測定。</param>
    /// <param name="up">上りの測定。測っていなければ null。</param>
    public static CheckResult Judge(
        CheckItem item, string proxyName, SpeedSample down, SpeedSample? up = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(down);

        if (down.Problem is { } problem)
            return Make(item, proxyName, CheckVerdict.Fail, problem, down.ElapsedMs);

        if (down.Bytes == 0)
            return Make(item, proxyName, CheckVerdict.Fail, "1 バイトも流れませんでした", down.ElapsedMs);

        string measured = Describe(down, up);
        double? expected = ParseExpected(item.Expect);

        if (expected is not { } target)
            return Make(item, proxyName, CheckVerdict.Pass, measured, down.ElapsedMs);

        // 目安は下りで見る。上りは参考として出すだけ
        // （業務アプリの体感はほぼ下りで決まるため）
        double actual = BitRateFormat.ToMbps(down.BytesPerSecond);

        return actual >= target
            ? Make(item, proxyName, CheckVerdict.Pass,
                   $"{measured}（目安 {Trim(target)} Mbps 以上）", down.ElapsedMs)
            : Make(item, proxyName, CheckVerdict.Warn,
                   $"{measured}　下りが目安 {Trim(target)} Mbps を下回っています", down.ElapsedMs);
    }

    /// <summary>測った値そのもの。合否に関わらず証跡には必ず残す。</summary>
    public static string Describe(SpeedSample down, SpeedSample? up = null)
    {
        ArgumentNullException.ThrowIfNull(down);

        string text = $"下り {BitRateFormat.Format(down.BytesPerSecond)}"
                    + $"（{FormatSize(down.Bytes)} / {down.ElapsedMs / 1000:0.0} 秒）";

        if (up is null) return text;

        // 上りが測れなかったことも証跡になる（上りだけ絞られている経路がある）
        return up.Measured
            ? text + $"・上り {BitRateFormat.Format(up.BytesPerSecond)}"
                   + $"（{FormatSize(up.Bytes)} / {up.ElapsedMs / 1000:0.0} 秒）"
            : text + $"・上りは測れませんでした（{up.Problem ?? "応答がありません"}）";
    }

    private static CheckResult Make(
        CheckItem item, string proxy, CheckVerdict verdict, string detail, double ms)
        => new(item.Name, item.Kind, item.Target, proxy, verdict, detail, ms);

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
