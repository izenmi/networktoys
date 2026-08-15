namespace PastelNet.Core.Quality;

/// <param name="RFactor">E-model の R 値（0〜100）。</param>
/// <param name="Mos">1.0〜4.5 の推定 MOS 値。</param>
/// <param name="Grade">現場で伝わる 5 段階の言い方。</param>
public readonly record struct VoiceQuality(double RFactor, double Mos, string Grade);

/// <summary>
/// 遅延・ジッタ・損失から通話品質の目安（MOS）を推定する。
///
/// ITU-T G.107 の E-model を、ネットワーク測定でよく使われる形に簡略化したもの。
/// <b>あくまで推定値</b>で、実際の音声品質はコーデックや機器にも左右される。
/// UI でもその旨を明記すること。
///
/// 純粋な計算なので、既知の入力と期待値でテストできる。
/// </summary>
public static class MosCalculator
{
    /// <param name="averageRttMs">平均往復時間。</param>
    /// <param name="jitterMs">ジッタ。</param>
    /// <param name="lossPercent">パケット損失率（%）。</param>
    public static VoiceQuality Estimate(double averageRttMs, double jitterMs, double lossPercent)
    {
        // 片道遅延に、ジッタバッファと機器の処理分を足した「実効遅延」
        double effectiveLatency = averageRttMs + jitterMs * 2 + 10;

        // 遅延が 160ms を超えると体感の悪化が急になるので、傾きを変える
        double r = effectiveLatency < 160
            ? 93.2 - effectiveLatency / 40
            : 93.2 - (effectiveLatency - 120) / 10;

        // 損失の影響。1% あたり 2.5 ポイント差し引く
        r -= lossPercent * 2.5;
        r = Math.Clamp(r, 0, 100);

        double mos = r == 0
            ? 1.0
            : 1 + 0.035 * r + r * (r - 60) * (100 - r) * 7e-6;

        mos = Math.Clamp(mos, 1.0, 4.5);

        return new VoiceQuality(r, mos, GradeOf(mos));
    }

    /// <summary>MOS を現場で伝わる言葉にする。</summary>
    public static string GradeOf(double mos) => mos switch
    {
        >= 4.3 => "非常に良い",
        >= 4.0 => "良い",
        >= 3.6 => "普通",
        >= 3.1 => "悪い",
        _ => "通話に耐えない",
    };
}
