using System.Globalization;
using PingWatcher.Core.Metrics;

namespace PingWatcher.Core.Verify;

/// <summary>
/// Teams の通話品質を、実際に音声が通る道（UDP のリレー）で測った値から判定する。
///
/// <b>ICMP の ping ではなく、通話が使う UDP で測ることに意味がある。</b>
/// 音声は経路も扱いも ICMP と違い、優先制御が効いていたり逆に絞られていたりする。
/// リレーへ問い合わせを繰り返して往復時間を集めれば、通話が通る道そのものの数字になる。
///
/// しきい値は Microsoft が公開している目安に合わせてある。
/// <b>割っていても「通話できない」わけではない</b>ので、不合格ではなく注意にする。
/// </summary>
public static class CallQuality
{
    /// <summary>往復の遅延。これを超えると声が重なりやすくなる。</summary>
    public const double MaxRoundTripMs = 200;

    /// <summary>ゆらぎ。これを超えると音が途切れて聞こえる。</summary>
    public const double MaxJitterMs = 30;

    /// <summary>失われた割合。これを超えると声が欠ける。</summary>
    public const double MaxLossPercent = 1.0;

    /// <summary>
    /// 判定して、証跡に出す 1 行を返す。
    /// </summary>
    /// <returns>
    /// <c>Acceptable</c> が false なら目安を割っている（通話はできるが品質に注意）。
    /// </returns>
    public static (bool Acceptable, string Text) Judge(RttStatistics stats)
    {
        var concerns = new List<string>();

        if (stats.Successes == 0)
            return (false, "応答が無く品質を測れませんでした");

        if (stats.AverageMs > MaxRoundTripMs)
            concerns.Add($"往復 {Format(stats.AverageMs)} が目安 {Format(MaxRoundTripMs)} を超えています");

        if (stats.JitterMs > MaxJitterMs)
            concerns.Add($"ゆらぎ {Format(stats.JitterMs)} が目安 {Format(MaxJitterMs)} を超えています");

        if (stats.LossPercent > MaxLossPercent)
            concerns.Add($"欠落 {stats.LossPercent.ToString("0.#", CultureInfo.InvariantCulture)}% が"
                       + $" 目安 {MaxLossPercent.ToString("0.#", CultureInfo.InvariantCulture)}% を超えています");

        string measured = Describe(stats);

        return concerns.Count == 0
            ? (true, measured)
            : (false, $"{measured}　{string.Join("・", concerns)}");
    }

    /// <summary>測った値そのもの。合否に関わらず証跡には必ず残す。</summary>
    public static string Describe(RttStatistics stats)
        => $"往復 {Format(stats.AverageMs)}（最短 {Format(stats.MinMs)} / 最長 {Format(stats.MaxMs)}）"
         + $"・ゆらぎ {Format(stats.JitterMs)}"
         + $"・欠落 {stats.LossPercent.ToString("0.#", CultureInfo.InvariantCulture)}%"
         + $"（{stats.Successes}/{stats.Attempts} 応答）";

    private static string Format(double ms)
        => ms.ToString(ms < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " ms";
}
