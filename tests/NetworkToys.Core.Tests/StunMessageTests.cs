using System.Net;
using NetworkToys.Core.Verify;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// Teams の音声が通るかは UDP 3478 で決まる。ただ投げるだけでは
/// 「開いている」と「塞がれている」を区別できないので、応答が返る STUN で確かめる。
/// ここはバイト列の組み立てと解析だけなので CI で固められる。
/// </summary>
public class StunMessageTests
{
    private static byte[] Id(byte seed = 1)
        => [.. Enumerable.Range(0, 12).Select(i => (byte)(seed + i))];

    [Fact]
    public void A_request_is_a_bare_20_byte_header()
    {
        byte[] id = Id();
        byte[] request = StunMessage.BuildRequest(id);

        Assert.Equal(20, request.Length);

        // 種別 0x0001 = Binding Request
        Assert.Equal(0x00, request[0]);
        Assert.Equal(0x01, request[1]);

        // 属性を付けないので長さは 0
        Assert.Equal(0x00, request[2]);
        Assert.Equal(0x00, request[3]);

        // マジッククッキー 0x2112A442
        Assert.Equal([0x21, 0x12, 0xA4, 0x42], request[4..8]);

        Assert.Equal(id, request[8..20]);
    }

    [Fact]
    public void The_transaction_id_must_be_12_bytes()
        => Assert.Throws<ArgumentException>(() => StunMessage.BuildRequest(new byte[11]));

    [Theory]
    [InlineData("203.0.113.9", 51234)]
    [InlineData("0.0.0.0", 1)]
    [InlineData("255.255.255.255", 65535)]
    public void A_reply_gives_back_the_address_seen_from_outside(string address, int port)
    {
        byte[] id = Id(7);
        var seen = new IPEndPoint(IPAddress.Parse(address), port);

        StunReply reply = StunMessage.ParseReply(StunMessage.BuildSuccessResponse(id, seen), id);

        Assert.True(reply.Success);
        Assert.Null(reply.Problem);
        Assert.Equal(seen, reply.MappedAddress);
    }

    [Fact]
    public void An_ipv6_address_is_unmasked_with_the_transaction_id_too()
    {
        byte[] id = Id(3);
        var seen = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 3478);

        StunReply reply = StunMessage.ParseReply(StunMessage.BuildSuccessResponse(id, seen), id);

        Assert.True(reply.Success);
        Assert.Equal(seen, reply.MappedAddress);
    }

    [Fact]
    public void A_reply_to_someone_elses_question_is_rejected()
    {
        // 同じポートに別の応答が紛れ込んだとき、それを成功と読んではいけない
        byte[] mine = Id(1);
        byte[] theirs = Id(200);

        StunReply reply = StunMessage.ParseReply(
            StunMessage.BuildSuccessResponse(theirs, new IPEndPoint(IPAddress.Loopback, 1)), mine);

        Assert.False(reply.Success);
        Assert.Contains("別の問い合わせ", reply.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Something_that_is_not_stun_is_rejected()
    {
        byte[] id = Id();
        byte[] junk = new byte[20];

        // 種別だけ合わせても、マジッククッキーが無ければ STUN ではない
        junk[0] = 0x01;
        junk[1] = 0x01;

        StunReply reply = StunMessage.ParseReply(junk, id);

        Assert.False(reply.Success);
        Assert.Contains("STUN の応答ではありません", reply.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_reply_is_rejected_without_reading_past_the_end()
    {
        StunReply reply = StunMessage.ParseReply(new byte[8], Id());

        Assert.False(reply.Success);
        Assert.Contains("短すぎます", reply.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reply_without_a_readable_address_still_counts_as_reachable()
    {
        // 属性が読めなくても「応答が返った」ことは確か。そこが知りたいことなので通す
        byte[] id = Id(5);
        byte[] response = StunMessage.BuildSuccessResponse(id, new IPEndPoint(IPAddress.Loopback, 1));

        // 属性の長さだけ壊す（ヘッダは正しいまま）
        response[2] = 0xFF;
        response[3] = 0xFF;

        StunReply reply = StunMessage.ParseReply(response, id);

        Assert.True(reply.Success);
    }
}
