using System.Globalization;
using System.Text;

namespace PingWatcher.Core.Snmp;

/// <summary>
/// varbind の値。BER のタグごとに型を分け、表示用の文字列を持つ。
/// 表示はカルチャ非依存。生のタグと中身も保持する（応答をそのまま書き戻すため）。
/// </summary>
public sealed class SnmpValue
{
    private SnmpValue(byte tag, string typeName, string display, byte[] raw)
    {
        Tag = tag;
        TypeName = typeName;
        Display = display;
        Raw = raw;
    }

    public byte Tag { get; }
    public string TypeName { get; }
    public string Display { get; }
    public byte[] Raw { get; }

    /// <summary>タグと中身から値を組み立てる。読めない中身でも「型と生バイト」で残す。</summary>
    public static SnmpValue From(byte tag, ReadOnlySpan<byte> content)
    {
        byte[] raw = content.ToArray();

        switch (tag)
        {
            case BerTag.Integer:
                return BerReader.TryReadInteger(content, out long i)
                    ? new SnmpValue(tag, "Integer", i.ToString(CultureInfo.InvariantCulture), raw)
                    : Unknown(tag, raw);

            case BerTag.OctetString:
                return new SnmpValue(tag, "OctetString", DescribeOctetString(content), raw);

            case BerTag.Null:
                return new SnmpValue(tag, "Null", string.Empty, raw);

            case BerTag.ObjectIdentifier:
                return BerReader.TryReadOid(content, out uint[] subs)
                    ? new SnmpValue(tag, "OID", new Oid(subs).DisplayName, raw)
                    : Unknown(tag, raw);

            case BerTag.IpAddress:
                return content.Length == 4
                    ? new SnmpValue(tag, "IpAddress", $"{content[0]}.{content[1]}.{content[2]}.{content[3]}", raw)
                    : Unknown(tag, raw);

            case BerTag.Counter32:
                return BerReader.TryReadUnsigned(content, out ulong c32)
                    ? new SnmpValue(tag, "Counter32", c32.ToString(CultureInfo.InvariantCulture), raw)
                    : Unknown(tag, raw);

            case BerTag.Gauge32:
                return BerReader.TryReadUnsigned(content, out ulong g32)
                    ? new SnmpValue(tag, "Gauge32", g32.ToString(CultureInfo.InvariantCulture), raw)
                    : Unknown(tag, raw);

            case BerTag.Counter64:
                return BerReader.TryReadUnsigned(content, out ulong c64)
                    ? new SnmpValue(tag, "Counter64", c64.ToString(CultureInfo.InvariantCulture), raw)
                    : Unknown(tag, raw);

            case BerTag.TimeTicks:
                return BerReader.TryReadUnsigned(content, out ulong ticks)
                    ? new SnmpValue(tag, "TimeTicks", FormatTimeTicks(ticks), raw)
                    : Unknown(tag, raw);

            case BerTag.NoSuchObject:
                return new SnmpValue(tag, "エラー", "この OID はありません（noSuchObject）", raw);
            case BerTag.NoSuchInstance:
                return new SnmpValue(tag, "エラー", "そのインスタンスはありません（noSuchInstance）", raw);
            case BerTag.EndOfMibView:
                return new SnmpValue(tag, "エラー", "MIB の末尾です（endOfMibView）", raw);

            default:
                return Unknown(tag, raw);
        }
    }

    /// <summary>TimeTicks（1/100 秒単位）を「1日 02:03:04.56」のように。</summary>
    public static string FormatTimeTicks(ulong hundredths)
    {
        ulong totalSeconds = hundredths / 100;
        ulong cs = hundredths % 100;
        ulong days = totalSeconds / 86400;
        ulong hours = totalSeconds % 86400 / 3600;
        ulong minutes = totalSeconds % 3600 / 60;
        ulong seconds = totalSeconds % 60;

        string time = string.Create(CultureInfo.InvariantCulture, $"{hours:D2}:{minutes:D2}:{seconds:D2}.{cs:D2}");
        return days > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{days}日 {time}")
            : time;
    }

    private static SnmpValue Unknown(byte tag, byte[] raw)
        => new(tag, string.Create(CultureInfo.InvariantCulture, $"0x{tag:X2}"), Convert.ToHexString(raw), raw);

    /// <summary>OCTET STRING は印字可能なら文字列、そうでなければ 16 進で見せる。</summary>
    private static string DescribeOctetString(ReadOnlySpan<byte> content)
    {
        bool printable = true;
        foreach (byte b in content)
        {
            // 制御文字（タブ・改行は許す）が混ざればバイナリ扱い
            if (b is < 0x20 and not (0x09 or 0x0A or 0x0D) || b == 0x7F)
            {
                printable = false;
                break;
            }
        }

        return printable ? Encoding.UTF8.GetString(content) : Convert.ToHexString(content);
    }
}
