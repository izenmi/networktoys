namespace NetworkToys.Core.Snmp;

/// <summary>
/// ASN.1 BER の書き出し。SNMP メッセージの組み立てに使う。
///
/// TLV（タグ・長さ・値）を積む。SEQUENCE のように中身の長さを先に
/// 知れない構造は、いったん子を書いてから包む形にする（<see cref="BeginSequence"/>）。
/// </summary>
public sealed class BerWriter
{
    private readonly List<byte> _bytes = [];

    public byte[] ToArray() => [.. _bytes];

    /// <summary>INTEGER（符号付き・最小バイト表現）。</summary>
    public void WriteInteger(long value)
    {
        Span<byte> tmp = stackalloc byte[8];
        int count = 0;

        // ビッグエンディアンで、先頭の冗長な 0x00/0xFF を落とした最小表現
        ulong bits = unchecked((ulong)value);
        for (int i = 7; i >= 0; i--)
            tmp[count++] = (byte)(bits >> (i * 8));

        int start = 0;
        while (start < count - 1
               && ((tmp[start] == 0x00 && (tmp[start + 1] & 0x80) == 0)
                   || (tmp[start] == 0xFF && (tmp[start + 1] & 0x80) != 0)))
        {
            start++;
        }

        WriteTag(BerTag.Integer, tmp[start..count]);
    }

    public void WriteOctetString(ReadOnlySpan<byte> value) => WriteTag(BerTag.OctetString, value);

    public void WriteNull() => WriteTag(BerTag.Null, []);

    /// <summary>OBJECT IDENTIFIER。サブ識別子列を受け取る。</summary>
    public void WriteOid(IReadOnlyList<uint> subIds)
    {
        var body = new List<byte>();

        // 先頭 2 つは 40*x+y に詰める（無ければ 0）
        uint first = subIds.Count > 0 ? subIds[0] : 0;
        uint second = subIds.Count > 1 ? subIds[1] : 0;
        AppendBase128(body, (first * 40) + second);

        for (int i = 2; i < subIds.Count; i++)
            AppendBase128(body, subIds[i]);

        WriteTag(BerTag.ObjectIdentifier, [.. body]);
    }

    /// <summary>生のタグと中身をそのまま積む（応答の値をそのまま書き戻すときなど）。</summary>
    public void WriteRaw(byte tag, ReadOnlySpan<byte> value) => WriteTag(tag, value);

    /// <summary>
    /// これまで書いた分を、指定タグ（既定 SEQUENCE）で包み直す。
    /// SNMP のメッセージ全体や PDU を組むときに、子を書き終えてから呼ぶ。
    /// </summary>
    public byte[] WrapAll(byte tag = BerTag.Sequence)
    {
        byte[] inner = [.. _bytes];
        _bytes.Clear();
        WriteTag(tag, inner);
        return ToArray();
    }

    private void WriteTag(byte tag, ReadOnlySpan<byte> value)
    {
        _bytes.Add(tag);
        WriteLength(value.Length);
        foreach (byte b in value)
            _bytes.Add(b);
    }

    private void WriteLength(int length)
    {
        if (length < 0x80)
        {
            _bytes.Add((byte)length);
            return;
        }

        // 長形式: 先頭バイトは 0x80 | バイト数、続けてビッグエンディアン
        Span<byte> tmp = stackalloc byte[4];
        int count = 0;
        for (int i = 3; i >= 0; i--)
        {
            byte b = (byte)(length >> (i * 8));
            if (count > 0 || b != 0)
                tmp[count++] = b;
        }

        _bytes.Add((byte)(0x80 | count));
        for (int i = 0; i < count; i++)
            _bytes.Add(tmp[i]);
    }

    private static void AppendBase128(List<byte> body, uint value)
    {
        Span<byte> tmp = stackalloc byte[5];
        int count = 0;

        do
        {
            tmp[count++] = (byte)(value & 0x7F);
            value >>= 7;
        }
        while (value > 0);

        // 上位から並べ、最終バイト以外は継続ビット(0x80)を立てる
        for (int i = count - 1; i >= 0; i--)
            body.Add((byte)(tmp[i] | (i > 0 ? 0x80 : 0x00)));
    }
}
