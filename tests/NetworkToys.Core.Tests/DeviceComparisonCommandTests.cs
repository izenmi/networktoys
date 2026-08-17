using NetworkToys.Core.Work;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// 差分比較の「機器から」で流す既定のコマンド。
/// <b>対象と違うコマンドを流すと、比べ方だけが構造化されて中身が噛み合わない</b>ので、
/// 対応を固定しておく。
/// </summary>
public class DeviceComparisonCommandTests
{
    [Theory]
    [InlineData(DeviceOutputKind.Configuration, "show running-config")]
    [InlineData(DeviceOutputKind.RouteTable, "show ip route")]
    [InlineData(DeviceOutputKind.InterfaceBrief, "show ip interface brief")]
    [InlineData(DeviceOutputKind.CdpNeighbors, "show cdp neighbors detail")]
    [InlineData(DeviceOutputKind.MacTable, "show mac address-table")]
    public void 対象ごとの既定のコマンド(DeviceOutputKind kind, string expected)
        => Assert.Equal(expected, DeviceComparison.CommandFor(kind));

    /// <summary>「そのまま比較」には決まった形が無い。勝手に何かを流さない。</summary>
    [Fact]
    public void そのまま比較には既定を持たせない()
        => Assert.Equal("", DeviceComparison.CommandFor(DeviceOutputKind.PlainText));

    /// <summary>既定はすべて読み取り。収集タブと同じ物差しで弾かれない。</summary>
    [Fact]
    public void 既定のコマンドはすべて読み取りである()
    {
        foreach (DeviceOutputKind kind in Enum.GetValues<DeviceOutputKind>())
        {
            string command = DeviceComparison.CommandFor(kind);

            if (command.Length == 0) continue;

            Assert.NotEqual(
                NetworkToys.Core.Terminal.CommandRisk.Blocked,
                NetworkToys.Core.Terminal.CiscoCommandGuard.Classify(command).Risk);
        }
    }
}
