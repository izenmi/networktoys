namespace PingWatcher.Core.Snmp;

/// <summary>BER のタグ値。SNMP で使うものだけ。</summary>
public static class BerTag
{
    // 汎用（universal）
    public const byte Integer = 0x02;
    public const byte OctetString = 0x04;
    public const byte Null = 0x05;
    public const byte ObjectIdentifier = 0x06;
    public const byte Sequence = 0x30;

    // SNMP のアプリケーションタグ（application, primitive）
    public const byte IpAddress = 0x40;
    public const byte Counter32 = 0x41;
    public const byte Gauge32 = 0x42;   // = Unsigned32
    public const byte TimeTicks = 0x43;
    public const byte Opaque = 0x44;
    public const byte Counter64 = 0x46;

    // 例外的な値（GETNEXT の終端など、v2c）
    public const byte NoSuchObject = 0x80;
    public const byte NoSuchInstance = 0x81;
    public const byte EndOfMibView = 0x82;

    // PDU タグ（context, constructed）
    public const byte GetRequest = 0xA0;
    public const byte GetNextRequest = 0xA1;
    public const byte GetResponse = 0xA2;
    public const byte SetRequest = 0xA3;
    public const byte TrapV1 = 0xA4;
    public const byte GetBulkRequest = 0xA5;
    public const byte TrapV2 = 0xA7;
}
