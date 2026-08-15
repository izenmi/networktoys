namespace PingWatcher.Core.Snmp;

/// <param name="Oid">対象 OID。</param>
/// <param name="Value">値。要求では Null。</param>
public sealed record VarBind(Oid Oid, SnmpValue Value);

/// <summary>SNMP のバージョン。数値はプロトコル上の値。</summary>
public enum SnmpVersion
{
    V1 = 0,
    V2c = 1,
}

/// <param name="Version">バージョン。</param>
/// <param name="Community">コミュニティ文字列。</param>
/// <param name="PduTag">PDU の種別（GetResponse / Trap など）。</param>
/// <param name="RequestId">リクエスト ID（Trap v1 では未使用）。</param>
/// <param name="ErrorStatus">エラー番号（0=正常）。</param>
/// <param name="ErrorIndex">エラーの起きた varbind の位置。</param>
/// <param name="VarBinds">値の並び。</param>
/// <param name="TrapOid">v2c Trap の snmpTrapOID（あれば）。</param>
public sealed record SnmpMessage(
    SnmpVersion Version,
    string Community,
    byte PduTag,
    int RequestId,
    int ErrorStatus,
    int ErrorIndex,
    IReadOnlyList<VarBind> VarBinds,
    Oid? TrapOid = null)
{
    /// <summary>error-status の日本語名。0 は正常。</summary>
    public string ErrorText => ErrorStatus switch
    {
        0 => string.Empty,
        1 => "大きすぎます（tooBig）",
        2 => "その OID はありません（noSuchName）",
        3 => "値が不正です（badValue）",
        4 => "読み取り専用です（readOnly）",
        5 => "一般エラー（genError）",
        6 => "アクセスできません（noAccess）",
        _ => $"エラー {ErrorStatus}",
    };
}

/// <summary>SNMP メッセージの組み立てと解析。</summary>
public static class SnmpCodec
{
    /// <summary>GetRequest / GetNextRequest を組む。</summary>
    public static byte[] BuildGet(SnmpVersion version, string community, int requestId, IReadOnlyList<Oid> oids, bool next)
    {
        // varbind list
        var varbindList = new BerWriter();
        foreach (Oid oid in oids)
        {
            var vb = new BerWriter();
            vb.WriteOid(oid.SubIds);
            vb.WriteNull();
            varbindList.WriteRaw(BerTag.Sequence, InnerOf(vb.WrapAll()));
        }

        // PDU
        var pdu = new BerWriter();
        pdu.WriteInteger(requestId);
        pdu.WriteInteger(0);   // error-status
        pdu.WriteInteger(0);   // error-index
        pdu.WriteRaw(BerTag.Sequence, InnerOf(varbindList.WrapAll()));
        byte[] pduBytes = pdu.WrapAll(next ? BerTag.GetNextRequest : BerTag.GetRequest);

        // message
        var message = new BerWriter();
        message.WriteInteger((long)version);
        message.WriteOctetString(System.Text.Encoding.ASCII.GetBytes(community));
        message.WriteRaw(pduBytes[0], InnerOf(pduBytes));
        return message.WrapAll();
    }

    /// <summary>受信したメッセージ（GetResponse / Trap）を解析する。読めなければ null。</summary>
    public static SnmpMessage? Parse(ReadOnlySpan<byte> data)
    {
        var outer = new BerReader(data);
        if (!outer.TryReadElement(out BerElement message) || message.Tag != BerTag.Sequence)
            return null;

        var body = new BerReader(message.Content);

        if (!body.TryReadElement(out BerElement versionEl) || versionEl.Tag != BerTag.Integer) return null;
        if (!BerReader.TryReadInteger(versionEl.Content, out long version)) return null;

        if (!body.TryReadElement(out BerElement communityEl) || communityEl.Tag != BerTag.OctetString) return null;
        string community = System.Text.Encoding.ASCII.GetString(communityEl.Content);

        if (!body.TryReadElement(out BerElement pdu)) return null;

        return pdu.Tag == BerTag.TrapV1
            ? ParseTrapV1((SnmpVersion)version, community, pdu.Content)
            : ParseStandardPdu((SnmpVersion)version, community, pdu.Tag, pdu.Content);
    }

    private static SnmpMessage? ParseStandardPdu(SnmpVersion version, string community, byte pduTag, ReadOnlySpan<byte> content)
    {
        var reader = new BerReader(content);

        if (!ReadInt(ref reader, out int requestId)) return null;
        if (!ReadInt(ref reader, out int errorStatus)) return null;
        if (!ReadInt(ref reader, out int errorIndex)) return null;

        if (!reader.TryReadElement(out BerElement vbList) || vbList.Tag != BerTag.Sequence) return null;

        List<VarBind> binds = ReadVarBinds(vbList.Content);

        // v2c Trap は先頭 2 つが sysUpTime と snmpTrapOID
        Oid? trapOid = pduTag == BerTag.TrapV2 && binds.Count >= 2 && binds[1].Value.Tag == BerTag.ObjectIdentifier
            ? BerReader.TryReadOid(binds[1].Value.Raw, out uint[] subs) ? new Oid(subs) : null
            : null;

        return new SnmpMessage(version, community, pduTag, requestId, errorStatus, errorIndex, binds, trapOid);
    }

    private static SnmpMessage? ParseTrapV1(SnmpVersion version, string community, ReadOnlySpan<byte> content)
    {
        var reader = new BerReader(content);

        // enterprise OID / agent-addr / generic-trap / specific-trap / time-stamp / varbinds
        if (!reader.TryReadElement(out BerElement enterprise) || enterprise.Tag != BerTag.ObjectIdentifier) return null;
        if (!reader.TryReadElement(out _)) return null;   // agent-addr
        if (!ReadInt(ref reader, out int generic)) return null;
        if (!ReadInt(ref reader, out int specific)) return null;
        if (!reader.TryReadElement(out _)) return null;   // time-stamp
        if (!reader.TryReadElement(out BerElement vbList) || vbList.Tag != BerTag.Sequence) return null;

        List<VarBind> binds = ReadVarBinds(vbList.Content);
        Oid? enterpriseOid = BerReader.TryReadOid(enterprise.Content, out uint[] subs) ? new Oid(subs) : null;

        // generic/specific を error-status/index の枠に載せて表示に使う
        return new SnmpMessage(version, community, BerTag.TrapV1, 0, generic, specific, binds, enterpriseOid);
    }

    private static List<VarBind> ReadVarBinds(ReadOnlySpan<byte> content)
    {
        var binds = new List<VarBind>();
        var reader = new BerReader(content);

        while (reader.TryReadElement(out BerElement vb))
        {
            if (vb.Tag != BerTag.Sequence) continue;

            var inner = new BerReader(vb.Content);
            if (!inner.TryReadElement(out BerElement oidEl) || oidEl.Tag != BerTag.ObjectIdentifier) continue;
            if (!inner.TryReadElement(out BerElement valueEl)) continue;
            if (!BerReader.TryReadOid(oidEl.Content, out uint[] subs)) continue;

            binds.Add(new VarBind(new Oid(subs), SnmpValue.From(valueEl.Tag, valueEl.Content)));
        }

        return binds;
    }

    private static bool ReadInt(ref BerReader reader, out int value)
    {
        value = 0;
        if (!reader.TryReadElement(out BerElement el) || el.Tag != BerTag.Integer) return false;
        if (!BerReader.TryReadInteger(el.Content, out long v)) return false;
        value = unchecked((int)v);
        return true;
    }

    /// <summary>TLV から V 部分だけを取り出す（WrapAll した結果を WriteRaw で入れ直すため）。</summary>
    private static byte[] InnerOf(byte[] tlv)
    {
        var reader = new BerReader(tlv);
        return reader.TryReadElement(out BerElement el) ? el.Content.ToArray() : [];
    }
}
