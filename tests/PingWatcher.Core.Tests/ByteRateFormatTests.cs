using PingWatcher.Core.Net;
using Xunit;

namespace PingWatcher.Core.Tests;

public class ByteRateFormatTests
{
    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(512, "512 B/s")]
    [InlineData(1023, "1023 B/s")]
    [InlineData(1024, "1.0 KB/s")]
    [InlineData(12595.2, "12.3 KB/s")]
    [InlineData(153600, "150 KB/s")]
    [InlineData(1048576, "1.0 MB/s")]
    [InlineData(4823449.6, "4.6 MB/s")]
    [InlineData(1073741824, "1.0 GB/s")]
    [InlineData(1099511627776, "1.0 TB/s")]
    [InlineData(-5, "0 B/s")]
    [InlineData(double.NaN, "0 B/s")]
    public void Rates_read_naturally(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, ByteRateFormat.Format(bytesPerSecond));
    }
}
