namespace NetworkToys.Core.Snmp;

/// <param name="Tag">TLV のタグ。</param>
/// <param name="Content">値の範囲（TLV の V 部分）。元バッファへのスライス。</param>
public readonly ref struct BerElement(byte Tag, ReadOnlySpan<byte> Content)
{
    public byte Tag { get; } = Tag;
    public ReadOnlySpan<byte> Content { get; } = Content;
}

/// <summary>
/// ASN.1 BER の読み取り。<b>例外を投げない。</b>機器の壊れた応答で落とさないよう、
/// 読めなければ <see cref="TryReadElement"/> が false を返す。
/// </summary>
public ref struct BerReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos = 0;

    public readonly bool AtEnd => _pos >= _data.Length;

    /// <summary>次の 1 つの TLV を読む。読み進める。</summary>
    public bool TryReadElement(out BerElement element)
    {
        element = default;

        if (_pos + 2 > _data.Length) return false;

        byte tag = _data[_pos++];

        if (!TryReadLength(out int length)) return false;
        if (_pos + length > _data.Length) return false;

        element = new BerElement(tag, _data.Slice(_pos, length));
        _pos += length;
        return true;
    }

    private bool TryReadLength(out int length)
    {
        length = 0;
        if (_pos >= _data.Length) return false;

        byte first = _data[_pos++];
        if ((first & 0x80) == 0)
        {
            length = first;   // 短形式
            return true;
        }

        int count = first & 0x7F;
        if (count is 0 or > 4) return false;         // 不定長・4 バイト超は扱わない
        if (_pos + count > _data.Length) return false;

        long value = 0;
        for (int i = 0; i < count; i++)
            value = (value << 8) | _data[_pos++];

        if (value > int.MaxValue) return false;

        length = (int)value;
        return true;
    }

    /// <summary>INTEGER の中身を符号付き long として読む。</summary>
    public static bool TryReadInteger(ReadOnlySpan<byte> content, out long value)
    {
        value = 0;
        if (content.Length is 0 or > 8) return false;

        // 先頭ビットで符号拡張
        value = (content[0] & 0x80) != 0 ? -1L : 0L;
        foreach (byte b in content)
            value = (value << 8) | b;

        return true;
    }

    /// <summary>符号なし整数（Counter32/Gauge32/TimeTicks/Counter64）として読む。</summary>
    public static bool TryReadUnsigned(ReadOnlySpan<byte> content, out ulong value)
    {
        value = 0;
        if (content.Length > 9) return false;   // 先頭 0x00 パディングを許して 9

        foreach (byte b in content)
            value = (value << 8) | b;

        return true;
    }

    /// <summary>OBJECT IDENTIFIER の中身をサブ識別子列に開く。</summary>
    public static bool TryReadOid(ReadOnlySpan<byte> content, out uint[] subIds)
    {
        subIds = [];
        if (content.Length == 0) return false;

        var list = new List<uint>();

        // 先頭バイト = 40*x + y
        int i = 0;
        if (!TryReadBase128(content, ref i, out uint firstPair)) return false;
        list.Add(firstPair / 40);
        list.Add(firstPair % 40);

        while (i < content.Length)
        {
            if (!TryReadBase128(content, ref i, out uint sub)) return false;
            list.Add(sub);
        }

        subIds = [.. list];
        return true;
    }

    private static bool TryReadBase128(ReadOnlySpan<byte> content, ref int i, out uint value)
    {
        value = 0;
        int read = 0;

        while (i < content.Length)
        {
            byte b = content[i++];
            value = (value << 7) | (uint)(b & 0x7F);
            read++;

            if ((b & 0x80) == 0)
                return true;

            if (read > 5) return false;   // 32bit を超える
        }

        return false;   // 継続ビットのまま終わった
    }
}
