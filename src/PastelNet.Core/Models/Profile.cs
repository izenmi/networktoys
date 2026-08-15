namespace PastelNet.Core.Models;

/// <summary>
/// 現場ごとの宛先セット。
///
/// 判定キーを SSID だけにしないのが肝。Windows 11 24H2 以降は位置情報の許可が
/// 無いと SSID を取得できないため、SSID に依存すると有線環境や未許可の環境で
/// まったく機能しなくなる。
/// </summary>
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>この現場のサブネット（CIDR 表記）。最も信頼できる判定材料。</summary>
    public string? SubnetCidr { get; set; }

    /// <summary>既定ゲートウェイの IP。</summary>
    public string? GatewayAddress { get; set; }

    /// <summary>既定ゲートウェイの MAC。同じ private アドレスを使う別拠点を見分けられる。</summary>
    public string? GatewayMac { get; set; }

    /// <summary>接続中の SSID。取得できたときだけ加点に使う。</summary>
    public string? Ssid { get; set; }

    /// <summary>この現場の宛先リスト（宛先タブと同じ書式のテキスト）。</summary>
    public string TargetListText { get; set; } = string.Empty;

    public bool IsValid() => !string.IsNullOrWhiteSpace(Name)
                             && (SubnetCidr is not null || GatewayAddress is not null || GatewayMac is not null || Ssid is not null);
}

/// <param name="SubnetCidr">今いるサブネット。</param>
/// <param name="GatewayAddress">既定ゲートウェイ。</param>
/// <param name="GatewayMac">既定ゲートウェイの MAC。</param>
/// <param name="Ssid">接続中の SSID（取れなければ null）。</param>
public readonly record struct NetworkFingerprint(
    string? SubnetCidr,
    string? GatewayAddress,
    string? GatewayMac,
    string? Ssid);

public static class ProfileMatcher
{
    /// <summary>この点数以上で「同じ現場」とみなす。ゲートウェイ一致だけでも届く重み付けにしてある。</summary>
    public const int MatchThreshold = 3;

    /// <summary>
    /// いまの接続環境がプロファイルにどれだけ合うかを点数にする。
    ///
    /// 重みは「間違えたときの困り方」で決めている。サブネットとゲートウェイは
    /// 現場ごとにほぼ一意なので重く、SSID は取れないことがあるので軽い。
    /// </summary>
    public static int Score(Profile profile, NetworkFingerprint current)
    {
        ArgumentNullException.ThrowIfNull(profile);

        int score = 0;

        if (Matches(profile.GatewayMac, current.GatewayMac)) score += 3;
        if (Matches(profile.SubnetCidr, current.SubnetCidr)) score += 3;
        if (Matches(profile.GatewayAddress, current.GatewayAddress)) score += 2;
        if (Matches(profile.Ssid, current.Ssid)) score += 1;

        return score;
    }

    /// <summary>最も合うプロファイルを返す。閾値に届くものが無ければ null。</summary>
    public static Profile? FindBest(IEnumerable<Profile> profiles, NetworkFingerprint current)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        Profile? best = null;
        int bestScore = 0;

        foreach (Profile profile in profiles)
        {
            int score = Score(profile, current);
            if (score > bestScore)
            {
                bestScore = score;
                best = profile;
            }
        }

        return bestScore >= MatchThreshold ? best : null;
    }

    private static bool Matches(string? expected, string? actual)
        => !string.IsNullOrEmpty(expected)
           && !string.IsNullOrEmpty(actual)
           && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}
