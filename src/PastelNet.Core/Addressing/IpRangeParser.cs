using System.Net;

namespace PastelNet.Core.Addressing;

public enum IpRangeKind
{
    /// <summary>範囲記法ではない（ホスト名や単一アドレス）。誤りではない。</summary>
    NotARange,

    /// <summary>範囲として展開できた。</summary>
    Expanded,

    /// <summary>範囲記法のつもりだが解釈できない、または多すぎる。</summary>
    Invalid,
}

/// <summary>
/// IPv4 の範囲記法を展開する。
///
/// ホスト名と紛れないよう、<b>数字とドットだけで構成された IPv4 形式</b>のときしか
/// 範囲とみなさない。`my-server` のようにハイフンを含むホスト名を範囲と誤解しない。
/// </summary>
public static class IpRangeParser
{
    /// <summary>一度に展開する既定の上限。/16 を誤って指定したときの事故を防ぐ。</summary>
    public const int DefaultLimit = 4096;

    public static IpRangeKind TryExpand(string token, int limit, out List<IPAddress> addresses, out string? error)
    {
        addresses = [];
        error = null;

        if (string.IsNullOrWhiteSpace(token))
            return IpRangeKind.NotARange;

        token = token.Trim();

        if (token.Contains('/', StringComparison.Ordinal))
            return ExpandCidr(token, limit, ref addresses, ref error);

        if (token.Contains('-', StringComparison.Ordinal))
            return ExpandDashRange(token, limit, ref addresses, ref error);

        return IpRangeKind.NotARange;
    }

    private static IpRangeKind ExpandCidr(string token, int limit, ref List<IPAddress> addresses, ref string? error)
    {
        string[] parts = token.Split('/');
        if (parts.Length != 2 || !TryParseIPv4(parts[0], out IPAddress? network))
            return IpRangeKind.NotARange;

        if (!int.TryParse(parts[1], out int prefix) || prefix is < 0 or > 32)
        {
            error = $"プレフィックス長が正しくありません: {token}";
            return IpRangeKind.Invalid;
        }

        uint first = IpMath.ToUInt32(IpMath.NetworkAddress(network, prefix));
        uint last = IpMath.ToUInt32(IpMath.BroadcastAddress(network, prefix));

        // /30 以下ではネットワークアドレスとブロードキャストを除く。
        // /31 は点対点、/32 は単一ホストなのでそのまま使う。
        if (prefix <= 30)
        {
            first++;
            last--;
        }

        return Fill(first, last, limit, token, ref addresses, ref error);
    }

    private static IpRangeKind ExpandDashRange(string token, int limit, ref List<IPAddress> addresses, ref string? error)
    {
        int dash = token.IndexOf('-', StringComparison.Ordinal);
        string head = token[..dash].Trim();
        string tail = token[(dash + 1)..].Trim();

        // 前半が IPv4 でなければホスト名とみなす（範囲記法ではない）
        if (!TryParseIPv4(head, out IPAddress? start))
            return IpRangeKind.NotARange;

        uint first = IpMath.ToUInt32(start);
        uint last;

        if (TryParseIPv4(tail, out IPAddress? end))
        {
            // 192.168.1.1-192.168.1.100
            last = IpMath.ToUInt32(end);
        }
        else if (byte.TryParse(tail, out byte lastOctet))
        {
            // 192.168.1.1-100（最終オクテットだけの短縮）
            last = (first & 0xFFFFFF00u) | lastOctet;
        }
        else
        {
            error = $"範囲の終わりを読み取れません: {token}";
            return IpRangeKind.Invalid;
        }

        if (last < first)
        {
            error = $"範囲の順序が逆です: {token}";
            return IpRangeKind.Invalid;
        }

        return Fill(first, last, limit, token, ref addresses, ref error);
    }

    private static IpRangeKind Fill(uint first, uint last, int limit, string token, ref List<IPAddress> addresses, ref string? error)
    {
        long count = (long)last - first + 1;

        if (count <= 0)
        {
            error = $"展開できるアドレスがありません: {token}";
            return IpRangeKind.Invalid;
        }

        if (count > limit)
        {
            error = $"{token} は {count:N0} 件になります（上限 {limit:N0} 件）。";
            return IpRangeKind.Invalid;
        }

        addresses = new List<IPAddress>((int)count);
        for (uint value = first; ; value++)
        {
            addresses.Add(IpMath.FromUInt32(value));
            if (value == last) break;   // last が uint.MaxValue でも回り込まないようにする
        }

        return IpRangeKind.Expanded;
    }

    /// <summary>
    /// 数字とドットだけの IPv4 表記のみ受け付ける。
    /// <see cref="IPAddress.TryParse"/> は "1" を 0.0.0.1 と解釈するなど寛容すぎるので使わない。
    /// </summary>
    public static bool TryParseIPv4(string text, out IPAddress? address)
    {
        address = null;

        string[] octets = text.Split('.');
        if (octets.Length != 4)
            return false;

        Span<byte> bytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (octets[i].Length == 0 || octets[i].Length > 3)
                return false;

            foreach (char c in octets[i])
            {
                if (!char.IsAsciiDigit(c))
                    return false;
            }

            if (!byte.TryParse(octets[i], out bytes[i]))
                return false;
        }

        address = new IPAddress(bytes);
        return true;
    }
}
