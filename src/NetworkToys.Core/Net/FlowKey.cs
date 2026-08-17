using System.Buffers.Binary;

namespace NetworkToys.Core.Net;

/// <summary>
/// ETW の通信イベントと接続表の行を突き合わせるためのキー。
/// 毎秒数千イベントが流れるので、文字列を作らないアロケーションゼロの構造体にする。
///
/// アドレスはビッグエンディアン読みの (Hi, Lo) で持ち、IPv4 は Lo の下位 32bit のみ。
/// IPv4-mapped IPv6（::ffff:a.b.c.d）は IPv4 キーへ畳む（デュアルスタックのソケットは
/// 接続表と ETW で表記が揺れるため）。IPv6 のスコープ ID は ETW 側に無いのでキーに含めない。
///
/// ETW イベントの saddr/daddr は方向によってローカル/リモートが揺れる報告があるため、
/// A/B に意味は持たせない。照合側が正順と <see cref="Swapped"/> の両方を引く。
/// </summary>
public readonly record struct FlowKey(
    bool Tcp,
    bool V6,
    uint Pid,
    ulong AHi,
    ulong ALo,
    ushort APort,
    ulong BHi,
    ulong BLo,
    ushort BPort)
{
    /// <param name="a">アドレス（IPv4 は 4 バイト、IPv6 は 16 バイト）。</param>
    /// <param name="b">相手側アドレス。長さは a と同じ。</param>
    public static FlowKey ForTcp(bool v6, uint pid, ReadOnlySpan<byte> a, ushort aPort, ReadOnlySpan<byte> b, ushort bPort)
    {
        (bool aV6, ulong aHi, ulong aLo) = Normalize(v6, a);
        (bool bV6, ulong bHi, ulong bLo) = Normalize(v6, b);
        return new FlowKey(true, aV6 || bV6, pid, aHi, aLo, aPort, bHi, bLo, bPort);
    }

    /// <summary>
    /// UDP はコネクションレスで接続表に相手側が無いため、片側のエンドポイントだけで
    /// キーを作る（相手側はゼロに畳む）。イベント側は saddr/daddr の両方をこの形で
    /// 積み、行側はローカルアドレスで引く。
    /// </summary>
    public static FlowKey ForUdp(bool v6, uint pid, ReadOnlySpan<byte> address, ushort port)
    {
        (bool nV6, ulong hi, ulong lo) = Normalize(v6, address);
        return new FlowKey(false, nV6, pid, hi, lo, port, 0, 0, 0);
    }

    /// <summary>A と B を入れ替えたキー。</summary>
    public FlowKey Swapped() => this with
    {
        AHi = BHi,
        ALo = BLo,
        APort = BPort,
        BHi = AHi,
        BLo = ALo,
        BPort = APort,
    };

    private static (bool V6, ulong Hi, ulong Lo) Normalize(bool v6, ReadOnlySpan<byte> address)
    {
        if (!v6)
            return (false, 0, BinaryPrimitives.ReadUInt32BigEndian(address));

        if (IsV4Mapped(address))
            return (false, 0, BinaryPrimitives.ReadUInt32BigEndian(address[12..]));

        return (true,
            BinaryPrimitives.ReadUInt64BigEndian(address),
            BinaryPrimitives.ReadUInt64BigEndian(address[8..]));
    }

    private static bool IsV4Mapped(ReadOnlySpan<byte> address)
    {
        for (int i = 0; i < 10; i++)
        {
            if (address[i] != 0) return false;
        }
        return address[10] == 0xFF && address[11] == 0xFF;
    }
}

/// <summary>1 フローぶんの累計バイト数。方向はイベント ID 由来（Pid のプロセスから見た送/受）。</summary>
public record struct FlowTotals(long Sent, long Received);
