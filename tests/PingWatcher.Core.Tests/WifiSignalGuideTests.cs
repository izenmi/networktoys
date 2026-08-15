using PingWatcher.Core.Metrics;
using Xunit;

namespace PingWatcher.Core.Tests;

public class WifiSignalGuideTests
{
    [Theory]
    [InlineData(-30, "非常に良い")]
    [InlineData(-50, "非常に良い")]
    [InlineData(-51, "良い")]
    [InlineData(-60, "良い")]
    [InlineData(-61, "実用圏")]
    [InlineData(-67, "実用圏")]
    [InlineData(-68, "弱い")]
    [InlineData(-75, "弱い")]
    [InlineData(-76, "不安定")]
    [InlineData(-90, "不安定")]
    public void Boundaries_read_as_expected(int rssi, string expected)
    {
        Assert.Equal(expected, WifiSignalGuide.Describe(rssi));
    }
}
