using System.Net;
using System.Net.Sockets;

namespace NetworkToys.Core.Addressing;

/// <summary>
/// IPv4 アドレスの数値変換とサブネット計算。
/// ネットワークにも UI にも依存しないので、そのままユニットテストできる。
/// </summary>
public static class IpMath
{
    /// <summary>IPv4 アドレスをホストバイトオーダの 32bit 値へ変換する。</summary>
    public static uint ToUInt32(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException($"IPv4 アドレスではありません: {address}", nameof(address));

        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out _))
            throw new ArgumentException($"アドレスを展開できません: {address}", nameof(address));

        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    /// <summary>32bit 値を IPv4 アドレスへ戻す。</summary>
    public static IPAddress FromUInt32(uint value)
    {
        Span<byte> bytes =
        [
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value,
        ];
        return new IPAddress(bytes);
    }

    /// <summary>プレフィックス長からサブネットマスクを作る。/0 は 0.0.0.0、/32 は 255.255.255.255。</summary>
    public static uint PrefixToMask(int prefixLength)
    {
        if (prefixLength is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(prefixLength), prefixLength, "プレフィックス長は 0〜32 です。");

        // uint を 32 シフトすると未定義動作になるため /0 は個別に扱う
        return prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
    }

    /// <summary>サブネットマスクからプレフィックス長を求める。連続しないマスクは受け付けない。</summary>
    public static int MaskToPrefix(IPAddress mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        // ここで検査しないと、ToUInt32 の中で paramName が "address" の例外になり
        // 呼び出し側のどの引数が悪いのか分からなくなる
        if (mask.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException($"IPv4 のマスクではありません: {mask}", nameof(mask));

        uint value = ToUInt32(mask);
        int prefix = System.Numerics.BitOperations.LeadingZeroCount(~value);
        if (prefix > 32) prefix = 32;

        if (PrefixToMask(prefix) != value)
            throw new ArgumentException($"連続したビットのマスクではありません: {mask}", nameof(mask));

        return prefix;
    }

    /// <summary>ネットワークアドレス(範囲の先頭)を返す。</summary>
    public static IPAddress NetworkAddress(IPAddress address, int prefixLength)
        => FromUInt32(ToUInt32(address) & PrefixToMask(prefixLength));

    /// <summary>ブロードキャストアドレス(範囲の末尾)を返す。</summary>
    public static IPAddress BroadcastAddress(IPAddress address, int prefixLength)
        => FromUInt32(ToUInt32(address) | ~PrefixToMask(prefixLength));

    /// <summary>
    /// スキャン対象となるホスト数。/31 と /32 は特別扱いで、ネットワーク／ブロードキャストを除外しない。
    /// </summary>
    public static long UsableHostCount(int prefixLength)
    {
        if (prefixLength is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(prefixLength), prefixLength, "プレフィックス長は 0〜32 です。");

        long total = 1L << (32 - prefixLength);
        return prefixLength >= 31 ? total : total - 2;
    }

    /// <summary>2 つのアドレスが同一サブネットに属するか。</summary>
    public static bool IsSameSubnet(IPAddress a, IPAddress b, int prefixLength)
    {
        uint mask = PrefixToMask(prefixLength);
        return (ToUInt32(a) & mask) == (ToUInt32(b) & mask);
    }
}
