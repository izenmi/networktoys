using PastelNet.Core.Addressing;

namespace PastelNet.Core.Work;

public enum NeighborChangeKind
{
    /// <summary>同じ IP の MAC が変わった。</summary>
    MacChanged,

    /// <summary>いなくなった。</summary>
    Disappeared,

    /// <summary>現れた。</summary>
    Appeared,
}

/// <param name="Address">対象の IP。</param>
/// <param name="BeforeMac">作業前の MAC。</param>
/// <param name="AfterMac">作業後の MAC。</param>
/// <param name="Kind">変化の種類。</param>
/// <param name="IsGateway">既定ゲートウェイか。ここの MAC 変化は冗長化の切り替わりを意味する。</param>
public sealed record NeighborChange(
    string Address,
    string? BeforeMac,
    string? AfterMac,
    NeighborChangeKind Kind,
    bool IsGateway)
{
    public string Describe() => Kind switch
    {
        NeighborChangeKind.MacChanged when IsGateway =>
            $"既定ゲートウェイ {Address} の MAC が {BeforeMac} から {AfterMac} に変わりました（冗長化の切り替わりの可能性）。",
        NeighborChangeKind.MacChanged => $"{Address} の MAC が {BeforeMac} から {AfterMac} に変わりました。",
        NeighborChangeKind.Disappeared => $"{Address} が近隣キャッシュから消えました。",
        _ => $"{Address}（{AfterMac}）が新しく現れました。",
    };
}

/// <summary>
/// 近隣キャッシュ（ARP）の前後を比べる。
///
/// <b>行の差分ではなく集合の差分にする。</b>近隣キャッシュはエージングで勝手に消えるため、
/// テキストとして比べると作業と無関係な増減でノイズが埋まる。
///
/// 得たいのは<b>「既定ゲートウェイの MAC が変わった」＝ VRRP/HSRP が切り替わった</b>という
/// 一行であって、キャッシュの入れ替わりではない。
/// </summary>
public static class NeighborDiff
{
    public static IReadOnlyList<NeighborChange> Compare(
        IReadOnlyDictionary<string, string>? before,
        IReadOnlyDictionary<string, string>? after,
        string? gatewayAddress = null)
    {
        before ??= new Dictionary<string, string>();
        after ??= new Dictionary<string, string>();

        var changes = new List<NeighborChange>();

        bool IsGateway(string address)
            => gatewayAddress is not null && string.Equals(address, gatewayAddress, StringComparison.Ordinal);

        foreach ((string address, string mac) in before)
        {
            if (ShouldSkip(address, mac))
                continue;

            if (after.TryGetValue(address, out string? currentMac))
            {
                if (!ShouldSkip(address, currentMac)
                    && !string.Equals(mac, currentMac, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add(new NeighborChange(address, mac, currentMac, NeighborChangeKind.MacChanged, IsGateway(address)));
                }
            }
            else
            {
                changes.Add(new NeighborChange(address, mac, null, NeighborChangeKind.Disappeared, IsGateway(address)));
            }
        }

        foreach ((string address, string mac) in after)
        {
            if (ShouldSkip(address, mac) || before.ContainsKey(address))
                continue;

            changes.Add(new NeighborChange(address, null, mac, NeighborChangeKind.Appeared, IsGateway(address)));
        }

        // ゲートウェイと MAC 変化を先に。消えた・現れたはキャッシュの都合でも起きる
        return
        [
            .. changes
                .OrderByDescending(c => c.IsGateway)
                .ThenBy(c => c.Kind)
                .ThenBy(c => SortKey(c.Address))
        ];
    }

    /// <summary>
    /// マルチキャストとブロードキャストは常に居るので比較から外す。
    /// </summary>
    private static bool ShouldSkip(string address, string mac)
    {
        if (string.IsNullOrEmpty(mac))
            return true;

        // ff-ff-ff-ff-ff-ff（ブロードキャスト）と 01-00-5e-...（IPv4 マルチキャスト）
        if (mac.StartsWith("FF-FF-FF", StringComparison.OrdinalIgnoreCase)
            || mac.StartsWith("01-00-5E", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IpRangeParser.TryParseIPv4(address, out System.Net.IPAddress? parsed))
            return true;

        uint value = IpMath.ToUInt32(parsed);

        // 224.0.0.0/4（マルチキャスト）と 255.255.255.255
        return value >= 0xE0000000u;
    }

    private static uint SortKey(string address)
        => IpRangeParser.TryParseIPv4(address, out System.Net.IPAddress? parsed) ? IpMath.ToUInt32(parsed) : uint.MaxValue;
}
