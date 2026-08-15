using System.Net;

namespace PingWatcher.Core.Addressing;

/// <param name="Network">ネットワークアドレス。</param>
/// <param name="Broadcast">ブロードキャストアドレス。</param>
/// <param name="PrefixLength">プレフィックス長。</param>
/// <param name="Mask">サブネットマスク（点区切り表記用）。</param>
/// <param name="FirstHost">使える最初のホスト。/31 /32 はネットワークアドレス自身。</param>
/// <param name="LastHost">使える最後のホスト。</param>
/// <param name="UsableHosts">使えるホスト数。</param>
public sealed record SubnetInfo(
    IPAddress Network,
    IPAddress Broadcast,
    int PrefixLength,
    IPAddress Mask,
    IPAddress FirstHost,
    IPAddress LastHost,
    long UsableHosts)
{
    public string Cidr => $"{Network}/{PrefixLength}";
}

/// <summary>
/// サブネット電卓の計算部。UI にもネットワークにも触れない純粋関数。
/// 範囲パーサ(<see cref="IpRangeParser"/>)とは目的が違う —
/// あちらは宛先の列挙、こちらは 1 つのサブネットの素性と分割案を出す。
/// </summary>
public static class SubnetCalculator
{
    /// <summary>
    /// 入力を解釈する。受け付ける書き方:
    ///   192.168.1.0/24
    ///   192.168.1.130 255.255.255.0   （IP とマスクを空白区切り）
    /// </summary>
    public static SubnetInfo? Parse(string? text, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(text))
            return null;

        string trimmed = text.Trim();
        IPAddress? address;
        int prefix;

        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            string[] parts = trimmed.Split('/');

            if (parts.Length != 2 || !IpRangeParser.TryParseIPv4(parts[0].Trim(), out address))
            {
                error = "IPv4 アドレスとして読み取れません。";
                return null;
            }

            if (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32)
            {
                error = "プレフィックス長は 0〜32 です。";
                return null;
            }
        }
        else
        {
            string[] tokens = trimmed.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 2
                && IpRangeParser.TryParseIPv4(tokens[0], out address)
                && IpRangeParser.TryParseIPv4(tokens[1], out IPAddress? mask))
            {
                try
                {
                    prefix = IpMath.MaskToPrefix(mask);
                }
                catch (ArgumentException)
                {
                    error = $"連続したビットのマスクではありません: {tokens[1]}";
                    return null;
                }
            }
            else
            {
                error = "「192.168.1.0/24」または「192.168.1.0 255.255.255.0」の形で入力してください。";
                return null;
            }
        }

        return Build(address, prefix);
    }

    /// <summary>1 段深いプレフィックスへ分割したときの子サブネット一覧。</summary>
    public static IReadOnlyList<SubnetInfo> Split(SubnetInfo parent, int newPrefixLength, int limit = 16)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (newPrefixLength <= parent.PrefixLength || newPrefixLength > 32)
            return [];

        // 個数は 2^(差分)。表示できる数だけ作れば十分なので全列挙はしない
        int count = SplitCount(parent.PrefixLength, newPrefixLength);
        uint size = newPrefixLength == 32 ? 1u : 1u << (32 - newPrefixLength);
        uint start = IpMath.ToUInt32(parent.Network);

        var result = new List<SubnetInfo>(Math.Min(count, limit));

        for (int i = 0; i < count && i < limit; i++)
            result.Add(Build(IpMath.FromUInt32(start + ((uint)i * size)), newPrefixLength));

        return result;
    }

    /// <summary>分割したときの子サブネットの個数。int に収まる範囲で返す。</summary>
    public static int SplitCount(int parentPrefix, int newPrefix)
    {
        int bits = newPrefix - parentPrefix;
        if (bits <= 0) return 0;

        return bits >= 31 ? int.MaxValue : 1 << bits;
    }

    private static SubnetInfo Build(IPAddress address, int prefix)
    {
        IPAddress network = IpMath.NetworkAddress(address, prefix);
        IPAddress broadcast = IpMath.BroadcastAddress(address, prefix);
        uint networkValue = IpMath.ToUInt32(network);
        uint broadcastValue = IpMath.ToUInt32(broadcast);

        // /31 は点対点、/32 は単一ホスト。ネットワーク/ブロードキャストを除かない
        (IPAddress first, IPAddress last) = prefix >= 31
            ? (network, broadcast)
            : (IpMath.FromUInt32(networkValue + 1), IpMath.FromUInt32(broadcastValue - 1));

        return new SubnetInfo(
            network,
            broadcast,
            prefix,
            IpMath.FromUInt32(IpMath.PrefixToMask(prefix)),
            first,
            last,
            IpMath.UsableHostCount(prefix));
    }
}
