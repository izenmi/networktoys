using NetworkToys.Core.Snmp;
using Xunit;

namespace NetworkToys.Core.Tests;

public class SnmpTests
{
    [Fact]
    public void An_oid_round_trips_through_dotted_text()
    {
        Oid? oid = Oid.Parse("1.3.6.1.2.1.1.1.0");

        Assert.NotNull(oid);
        Assert.Equal("1.3.6.1.2.1.1.1.0", oid.Text);
        Assert.Equal("sysDescr", oid.DisplayName);
    }

    [Fact]
    public void A_large_sub_identifier_survives_ber_encoding()
    {
        // 128 以上のサブ識別子は base-128 の可変長になる
        var oid = new Oid([1, 3, 6, 1, 4, 1, 9999]);

        var writer = new BerWriter();
        writer.WriteOid(oid.SubIds);
        byte[] tlv = writer.ToArray();

        var reader = new BerReader(tlv);
        Assert.True(reader.TryReadElement(out BerElement el));
        Assert.Equal(BerTag.ObjectIdentifier, el.Tag);
        Assert.True(BerReader.TryReadOid(el.Content, out uint[] subs));
        Assert.Equal(oid.SubIds, subs);
    }

    [Fact]
    public void A_get_request_round_trips_through_parse()
    {
        Oid oid = Oid.Parse("1.3.6.1.2.1.1.5.0")!;
        byte[] packet = SnmpCodec.BuildGet(SnmpVersion.V2c, "public", requestId: 12345, [oid], next: false);

        SnmpMessage? parsed = SnmpCodec.Parse(packet);

        Assert.NotNull(parsed);
        Assert.Equal(SnmpVersion.V2c, parsed.Version);
        Assert.Equal("public", parsed.Community);
        Assert.Equal(BerTag.GetRequest, parsed.PduTag);
        Assert.Equal(12345, parsed.RequestId);
        VarBind vb = Assert.Single(parsed.VarBinds);
        Assert.Equal(oid, vb.Oid);
        Assert.Equal(BerTag.Null, vb.Value.Tag);
    }

    [Fact]
    public void A_get_response_with_a_string_value_is_parsed()
    {
        // 実機の GetResponse を模したバイト列（community=public, sysName="sw01"）
        byte[] response =
        [
            0x30, 0x2b,
              0x02, 0x01, 0x01,               // version v2c
              0x04, 0x06, .. "public"u8,      // community
              0xa2, 0x1e,                     // GetResponse
                0x02, 0x02, 0x30, 0x39,       // request-id 12345
                0x02, 0x01, 0x00,             // error-status 0
                0x02, 0x01, 0x00,             // error-index 0
                0x30, 0x12,                   // varbind list
                  0x30, 0x10,                 // varbind
                    0x06, 0x08, 0x2b, 0x06, 0x01, 0x02, 0x01, 0x01, 0x05, 0x00,  // 1.3.6.1.2.1.1.5.0
                    0x04, 0x04, .. "sw01"u8,  // OCTET STRING "sw01"
        ];

        SnmpMessage? parsed = SnmpCodec.Parse(response);

        Assert.NotNull(parsed);
        Assert.Equal(0, parsed.ErrorStatus);
        VarBind vb = Assert.Single(parsed.VarBinds);
        Assert.Equal("sysName", vb.Oid.DisplayName);
        Assert.Equal("OctetString", vb.Value.TypeName);
        Assert.Equal("sw01", vb.Value.Display);
    }

    [Fact]
    public void An_error_status_is_read()
    {
        byte[] response =
        [
            0x30, 0x19,
              0x02, 0x01, 0x01,
              0x04, 0x06, .. "public"u8,
              0xa2, 0x0c,
                0x02, 0x02, 0x30, 0x39,
                0x02, 0x01, 0x02,             // error-status noSuchName
                0x02, 0x01, 0x01,             // error-index 1
                0x30, 0x00,                   // 空 varbind list
        ];

        SnmpMessage? parsed = SnmpCodec.Parse(response);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.ErrorStatus);
        Assert.Contains("noSuchName", parsed.ErrorText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 0x30 })]                 // 短すぎ
    [InlineData(new byte[] { 0x30, 0x05, 0x02 })]     // 長さと中身が合わない
    [InlineData(new byte[] { 0x02, 0x01, 0x01 })]     // SEQUENCE でない
    public void A_broken_packet_returns_null_not_an_exception(byte[] packet)
    {
        Assert.Null(SnmpCodec.Parse(packet));
    }

    [Theory]
    [InlineData(0ul, "00:00:00.00")]
    [InlineData(12345ul, "00:02:03.45")]
    [InlineData(8640000ul, "1日 00:00:00.00")]
    public void Time_ticks_are_human_readable(ulong hundredths, string expected)
    {
        Assert.Equal(expected, SnmpValue.FormatTimeTicks(hundredths));
    }

    [Fact]
    public void The_integer_encoding_is_minimal_and_signed()
    {
        var writer = new BerWriter();
        writer.WriteInteger(255);   // 0x00 0xFF（先頭ビットが立つので 0 パディングが要る）
        byte[] tlv = writer.ToArray();

        Assert.Equal(new byte[] { 0x02, 0x02, 0x00, 0xFF }, tlv);

        var reader = new BerReader(tlv);
        Assert.True(reader.TryReadElement(out BerElement el));
        Assert.True(BerReader.TryReadInteger(el.Content, out long value));
        Assert.Equal(255, value);
    }

    [Fact]
    public void A_walk_prefix_check_works()
    {
        Oid root = Oid.Parse("1.3.6.1.2.1.2.2.1.2")!;   // ifDescr

        Assert.True(Oid.Parse("1.3.6.1.2.1.2.2.1.2.1")!.IsDescendantOf(root));
        Assert.False(Oid.Parse("1.3.6.1.2.1.2.2.1.3.1")!.IsDescendantOf(root));
    }
}
