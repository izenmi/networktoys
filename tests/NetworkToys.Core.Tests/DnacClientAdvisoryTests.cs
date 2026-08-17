using NetworkToys.Core.Assurance;
using NetworkToys.Core.Design;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// 端末の一覧と脆弱性（PSIRT）の読み取り。
/// どちらも実機でしか形が分からないので、<b>見本を置いて読み方を固定</b>しておく。
/// </summary>
public class DnacClientAdvisoryTests
{
    [Fact]
    public void 端末の一覧は平らな形でも同じ行になる()
    {
        const string json = """
            {"response":[{
              "id":"AA:BB:CC:DD:EE:03","macAddress":"AA:BB:CC:DD:EE:03","ipv4Address":"10.30.1.9",
              "name":"pc-9","type":"WIRED","vlanId":"120","siteHierarchy":"Global/Osaka/2F",
              "lastUpdatedTime":1723852800000,
              "health":{"overallScore":9},
              "connectedNetworkDevice":{"connectedNetworkDeviceName":"sw-2f-01",
                                        "connectedInterfaceName":"GigabitEthernet1/0/7"}}]}
            """;

        DnacConnectionRow row = Assert.Single(DnacCatalog.ParseClients(DnacJson.Rows(json)));

        Assert.Equal("AA:BB:CC:DD:EE:03", row.Mac);
        Assert.Equal("10.30.1.9", row.Ip);
        Assert.Equal("有線", row.Kind);
        Assert.Equal("sw-2f-01", row.Device);
        Assert.Equal("GigabitEthernet1/0/7", row.Port);
        Assert.Equal("120", row.Vlan);
        Assert.Equal(SeverityKind.Ok, row.HealthKind);
        Assert.Equal("Global/Osaka/2F", row.Site);
        Assert.NotEqual("", row.Updated);
    }

    [Fact]
    public void 無線の端末は_AP_と_SSID_と帯域が埋まる()
    {
        const string json = """
            {"response":[{"macAddress":"AA:BB:CC:DD:EE:04","type":"WIRELESS","ssid":"CorpSSID",
              "band":"5.0","connectedNetworkDeviceName":"ap-5f-03","health":{"overallScore":5}}]}
            """;

        DnacConnectionRow row = Assert.Single(DnacCatalog.ParseClients(DnacJson.Rows(json)));

        Assert.Equal("無線", row.Kind);
        Assert.Equal("ap-5f-03", row.Device);
        Assert.Equal("CorpSSID", row.Ssid);
        Assert.Equal("5GHz", row.Band);
        Assert.Equal(SeverityKind.Notice, row.HealthKind);
    }

    [Fact]
    public void 端末の一覧の_offset_も_1_始まり()
    {
        Assert.Contains("offset=1&", DnacCatalog.ClientsPath(1, 2, 0, 500), StringComparison.Ordinal);
        Assert.Contains("offset=501&", DnacCatalog.ClientsPath(1, 2, 1, 500), StringComparison.Ordinal);
        Assert.Contains("startTime=1&endTime=2", DnacCatalog.ClientsPath(1, 2, 0, 500), StringComparison.Ordinal);
    }

    [Fact]
    public void 脆弱性は勧告_1_本を_1_行にする()
    {
        const string json = """
            {"response":[{
              "advisoryId":"cisco-sa-20260701-example","deviceCount":12,"sir":"Critical",
              "cvssBaseScore":"9.8","cves":["CVE-2026-1111","CVE-2026-2222"],
              "fixedVersions":["17.9.5"],"publicationUrl":"https://example.invalid/advisory"}]}
            """;

        DnacLifecycleRow row = Assert.Single(DnacCatalog.ParseAdvisories(DnacJson.Rows(json)));

        Assert.Equal("対象 12 台", row.Device);
        Assert.Equal("cisco-sa-20260701-example", row.Kind);
        Assert.Equal(SeverityKind.Alert, row.StateKind);
        Assert.Contains("9.8", row.State, StringComparison.Ordinal);
        Assert.Equal("17.9.5", row.Date);
        Assert.Contains("CVE-2026-1111", row.Note, StringComparison.Ordinal);
        Assert.Contains("CVE-2026-2222", row.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Critical", SeverityKind.Alert)]
    [InlineData("High", SeverityKind.Alert)]
    [InlineData("Medium", SeverityKind.Notice)]
    [InlineData("Low", SeverityKind.Ok)]
    [InlineData("", SeverityKind.Muted)]
    [InlineData("Something New", SeverityKind.Muted)]
    public void 勧告の重大度を読ませる(string severity, SeverityKind expected)
        => Assert.Equal(expected, DnacCatalog.DescribeAdvisorySeverity(severity).Kind);

    /// <summary>
    /// ライセンスは <c>page_number</c> / <c>limit</c> / <c>order</c> が無いと 400 になる
    /// （2026-08-17 に実機で確認）。抜けたら気づけるようにしておく。
    /// </summary>
    [Fact]
    public void ライセンスの必須クエリが付いている()
    {
        string path = DnacCatalog.LicensePaths[0];

        Assert.Contains("page_number=1", path, StringComparison.Ordinal);
        Assert.Contains("limit=", path, StringComparison.Ordinal);
        Assert.Contains("order=", path, StringComparison.Ordinal);

        Assert.All(DnacCatalog.AdvisoryPaths, p => Assert.StartsWith("/dna/", p, StringComparison.Ordinal));
    }
}
