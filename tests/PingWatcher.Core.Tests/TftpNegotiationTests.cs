using PingWatcher.Core.Tftp;
using Xunit;

namespace PingWatcher.Core.Tests;

public class TftpNegotiationTests
{
    private static Dictionary<string, string> Options(params string[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < pairs.Length; i += 2)
            d[pairs[i]] = pairs[i + 1];
        return d;
    }

    [Fact]
    public void No_options_falls_back_to_the_defaults()
    {
        TftpTransferOptions result = TftpNegotiation.Negotiate(Options(), transferSize: 0);

        Assert.Equal(TftpNegotiation.DefaultBlockSize, result.BlockSize);
        Assert.Empty(result.Accepted);   // OACK を送らない
    }

    [Fact]
    public void A_requested_block_size_is_accepted_and_echoed()
    {
        TftpTransferOptions result = TftpNegotiation.Negotiate(Options("blksize", "1468"), 0);

        Assert.Equal(1468, result.BlockSize);
        Assert.Equal("1468", result.Accepted["blksize"]);
    }

    [Theory]
    [InlineData("1", TftpNegotiation.MinBlockSize)]
    [InlineData("100000", TftpNegotiation.MaxBlockSize)]
    [InlineData("abc", TftpNegotiation.DefaultBlockSize)]
    public void An_out_of_range_or_broken_block_size_is_clamped(string requested, int expected)
    {
        TftpTransferOptions result = TftpNegotiation.Negotiate(Options("blksize", requested), 0);

        Assert.Equal(expected, result.BlockSize);
    }

    [Fact]
    public void Tsize_is_answered_with_the_real_size_on_read()
    {
        TftpTransferOptions result = TftpNegotiation.Negotiate(Options("tsize", "0"), transferSize: 4096);

        Assert.Equal("4096", result.Accepted["tsize"]);
    }

    [Fact]
    public void A_broken_block_size_is_not_echoed()
    {
        // 読めない値は交渉不成立。OACK に含めず、相手は既定 512 で続ける
        TftpTransferOptions result = TftpNegotiation.Negotiate(Options("blksize", "abc"), 0);

        Assert.False(result.Accepted.ContainsKey("blksize"));
    }
}
