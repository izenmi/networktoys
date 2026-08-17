using System.Buffers.Binary;
using System.Net;

namespace NetworkToys.Core.Verify;

/// <summary>STUN の応答から読み取れたもの。</summary>
/// <param name="Success">Binding Success Response だったか。</param>
/// <param name="MappedAddress">外から見えているアドレス。読めなければ null。</param>
/// <param name="Problem">読めなかった理由。成功なら null。</param>
public sealed record StunReply(bool Success, IPEndPoint? MappedAddress, string? Problem);

/// <summary>
/// STUN（RFC 5389）の Binding Request を組み立て、応答を解く。
///
/// <b>なぜ STUN なのか。</b>Teams の音声・映像は UDP 3478〜3481 を使う。
/// ただの UDP を投げても、無応答が「開いている（相手が黙っている）」のか
/// 「塞がれている」のか区別できない。STUN は<b>応答を返す</b>ので、
/// 返ってくれば通っていると確実に言い切れる。
///
/// おまけに応答には<b>外から見えている自分のアドレス</b>が入っているので、
/// 証跡としてそのまま使える。
///
/// ここは<b>バイト列の組み立てと解析だけ</b>。ソケットには触らないので CI で検査できる。
/// </summary>
public static class StunMessage
{
    /// <summary>Binding Request。</summary>
    private const ushort BindingRequest = 0x0001;

    /// <summary>Binding Success Response。</summary>
    private const ushort BindingSuccess = 0x0101;

    /// <summary>RFC 5389 で決まっている値。これが無い応答は STUN ではない。</summary>
    private const uint MagicCookie = 0x2112A442;

    private const int HeaderLength = 20;
    private const int TransactionIdLength = 12;

    private const ushort AttrMappedAddress = 0x0001;
    private const ushort AttrXorMappedAddress = 0x0020;

    /// <summary>ヘッダだけの Binding Request を組み立てる。属性は付けない。</summary>
    /// <param name="transactionId">12 バイト。呼ぶ側が乱数で作って応答の照合に使う。</param>
    public static byte[] BuildRequest(ReadOnlySpan<byte> transactionId)
    {
        if (transactionId.Length != TransactionIdLength)
            throw new ArgumentException($"トランザクション ID は {TransactionIdLength} バイト", nameof(transactionId));

        var message = new byte[HeaderLength];

        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0), BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 0);   // 属性なし
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), MagicCookie);
        transactionId.CopyTo(message.AsSpan(8));

        return message;
    }

    /// <summary>
    /// 応答を解く。
    ///
    /// <b>投げた要求と同じトランザクション ID でなければ受け付けない。</b>
    /// 同じポートに別の応答が紛れ込んだときに、それを成功と誤読しないため。
    /// </summary>
    public static StunReply ParseReply(ReadOnlySpan<byte> data, ReadOnlySpan<byte> transactionId)
    {
        if (data.Length < HeaderLength)
            return new StunReply(false, null, "応答が短すぎます。");

        ushort type = BinaryPrimitives.ReadUInt16BigEndian(data);
        if (type != BindingSuccess)
            return new StunReply(false, null, $"想定しない種別の応答です（0x{type:X4}）。");

        if (BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != MagicCookie)
            return new StunReply(false, null, "STUN の応答ではありません。");

        if (!data.Slice(8, TransactionIdLength).SequenceEqual(transactionId))
            return new StunReply(false, null, "別の問い合わせに対する応答です。");

        // 属性は読めなくても「通った」ことは確か。アドレスだけ null にして成功を返す
        int length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        int end = Math.Min(HeaderLength + length, data.Length);

        return new StunReply(true, FindAddress(data, HeaderLength, end), null);
    }

    /// <summary>属性を舐めて XOR-MAPPED-ADDRESS（無ければ MAPPED-ADDRESS）を拾う。</summary>
    private static IPEndPoint? FindAddress(ReadOnlySpan<byte> data, int start, int end)
    {
        IPEndPoint? plain = null;

        int at = start;
        while (at + 4 <= end)
        {
            ushort attribute = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            int size = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
            int value = at + 4;

            if (size < 0 || value + size > end) break;

            switch (attribute)
            {
                case AttrXorMappedAddress when TryReadAddress(data.Slice(value, size), data, xor: true) is { } xored:
                    return xored;   // こちらが本命。見つけ次第返す

                case AttrMappedAddress when TryReadAddress(data.Slice(value, size), data, xor: false) is { } bare:
                    plain ??= bare;
                    break;
            }

            // 属性は 4 バイト境界に詰められる
            at = value + ((size + 3) & ~3);
        }

        return plain;
    }

    /// <summary>
    /// アドレス属性を読む。<c>0 / family / port / address</c> の並び。
    ///
    /// XOR 版はポートをマジッククッキーの上位 16 ビットと、アドレスを
    /// クッキー（IPv6 はクッキー＋トランザクション ID）と XOR してある。
    /// 経路上の機器がアドレスを書き換えてしまうのを避けるための仕掛け。
    /// </summary>
    private static IPEndPoint? TryReadAddress(ReadOnlySpan<byte> value, ReadOnlySpan<byte> message, bool xor)
    {
        if (value.Length < 4) return null;

        int family = value[1];
        int size = family switch { 0x01 => 4, 0x02 => 16, _ => 0 };
        if (size == 0 || value.Length < 4 + size) return null;

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
        Span<byte> address = stackalloc byte[size];
        value.Slice(4, size).CopyTo(address);

        if (xor)
        {
            port ^= (ushort)(MagicCookie >> 16);

            // IPv4 はクッキーの 4 バイト、IPv6 はクッキー＋トランザクション ID の 16 バイト
            for (int i = 0; i < size; i++)
                address[i] ^= message[4 + i];
        }

        return new IPEndPoint(new IPAddress(address), port);
    }

    /// <summary>
    /// 検査用に、要求へ返す成功応答を組み立てる。
    /// 自己診断の偽 STUN サーバが使う（実機も外部通信も要らずに経路を通すため）。
    /// </summary>
    public static byte[] BuildSuccessResponse(ReadOnlySpan<byte> transactionId, IPEndPoint seenFrom)
    {
        ArgumentNullException.ThrowIfNull(seenFrom);

        byte[] raw = seenFrom.Address.GetAddressBytes();
        int size = raw.Length;

        var message = new byte[HeaderLength + 4 + 4 + size];

        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0), BindingSuccess);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)(4 + 4 + size));
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), MagicCookie);
        transactionId.CopyTo(message.AsSpan(8));

        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(HeaderLength), AttrXorMappedAddress);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(HeaderLength + 2), (ushort)(4 + size));

        message[HeaderLength + 4] = 0;
        message[HeaderLength + 5] = (byte)(size == 4 ? 0x01 : 0x02);
        BinaryPrimitives.WriteUInt16BigEndian(
            message.AsSpan(HeaderLength + 6), (ushort)(seenFrom.Port ^ (MagicCookie >> 16)));

        for (int i = 0; i < size; i++)
            message[HeaderLength + 8 + i] = (byte)(raw[i] ^ message[4 + i]);

        return message;
    }
}
